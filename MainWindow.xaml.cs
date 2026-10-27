using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RtlTerminal;

public partial class MainWindow : Window
{
    private readonly object _renderLock = new();
    private readonly DispatcherTimer _resizeTimer;
    private readonly List<TerminalTab> _tabs = [];
    private readonly List<string> _temporaryClipboardFiles = [];
    private TerminalTab? _activeTab;
    private ConPtySession? _session;
    private TerminalBuffer? _terminalBuffer;
    private CancellationTokenSource? _cancellationTokenSource;
    private TerminalSnapshot? _pendingSnapshot;
    private bool _renderStartQueued;
    private bool _updatingContextMenuItem;
    private long _latestQueuedRevision;
    private TerminalSnapshot? _lastRenderedSnapshot;
    private double _cellWidth = 8.5;
    private double _lineHeight = 18;
    private bool _followOutput = true;
    private bool _restoringScrollPosition;
    private int _nextTabNumber = 1;
    private bool _renderedSmartRtlEnabled = true;
    private TerminalProfile _defaultProfile = TerminalProfile.PowerShell;
    private int _historySize = 2000;
    private bool _updateCheckInProgress;
    private bool _suppressRightMouseUp;
    private Key? _pendingContextMenuKey;
    private (int X, int Y, int Button)? _lastReportedMouseCell;
    private int? _forwardedPressButton;

    public MainWindow()
    {
        InitializeComponent();
        this.Activated += (_, _) =>
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                TerminalTextBox.Focus();
                System.Windows.Input.Keyboard.Focus(TerminalTextBox);
            }));
        };
        TerminalTextBox.LinkRequested += uri =>
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception exception)
            { MessageBox.Show(this, exception.Message, "Open link", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        SmartRtlMenuItem.IsChecked = true;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyViewportResize(); };
        _defaultProfile = LoadDefaultProfile();
        _historySize = AppSettings.LoadHistorySize();
        ApplySavedFontSettings();
        UpdateFontMetrics();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshContextMenuIntegrationState();
        PromptForContextMenuIntegration();
        TerminalTextBox.Focus();
        TerminalTextBox.UpdateLayout();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => CreateTerminalTab(_defaultProfile));
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void CreateTerminalTab(
        TerminalProfile profile,
        string? requestedStartupDirectory = null)
    {
        SaveActiveTabState();
        var startupDirectory = ResolveStartupDirectory(
            requestedStartupDirectory);

        var tab = new TerminalTab(
            _nextTabNumber++,
            profile,
            GetProfileTitle(profile));
        _tabs.Add(tab);
        _activeTab = tab;
        LoadTabState(tab);
        RebuildTabStrip();

        try
        {
            var columns = GetColumns();
            var rows = GetRows();
            _terminalBuffer = new TerminalBuffer(
                columns,
                rows,
                _historySize);
            _cancellationTokenSource = new CancellationTokenSource();
            _session = new ConPtySession(columns, rows);
            _session.Start(
                GetProfileCommand(profile),
                startupDirectory);

            if (profile == TerminalProfile.CommandPrompt &&
                startupDirectory is not null)
            {
                AppSettings.RememberCmdDirectory(startupDirectory);
            }

            SaveActiveTabState();
            _ = Task.Run(() => ReadOutputLoop(tab));
        }
        catch (Exception exception)
        {
            // Do not leave a half-started pseudoconsole (and its pipes) behind:
            // the tab stays open but inert with a readable error instead.
            try
            {
                _session?.Dispose();
            }
            catch (Exception disposeException)
            {
                InputDebug.Log($"ConPTY cleanup failed: {disposeException.Message}");
            }

            _session = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            var errorBuffer = new TerminalBuffer(GetColumns(), GetRows());
            _terminalBuffer = errorBuffer;
            _activeTab.Buffer = errorBuffer;
            _activeTab.Session = null;
            Render(errorBuffer.Process("خطا در اجرای ConPTY:\r\n" + exception.Message));
        }

        TerminalTextBox.Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SaveActiveTabState();

        // Let every pseudoconsole shut down before the process exits, otherwise
        // a cmd.exe/powershell.exe child can outlive the window.
        var disposals = _tabs.Select(tab => tab.DisposeAsync()).ToArray();

        try
        {
            Task.WaitAll(disposals, TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            InputDebug.Log($"Shutdown cleanup failed: {exception.Message}");
        }

        CleanupTemporaryClipboardFiles();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => WindowWorkArea.Attach(this);

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M2.5,0.5 L9.5,0.5 9.5,7.5 M0.5,2.5 L7.5,2.5 7.5,9.5 0.5,9.5 Z"
            : "M0.5,0.5 L9.5,0.5 9.5,9.5 0.5,9.5 Z");

        QueueViewportResize();
    }

    private void QueueViewportResize()
    {
        if (_resizeTimer is null) return;
        _resizeTimer.Stop();
        if (WindowState != WindowState.Minimized) _resizeTimer.Start();
    }

    private void ApplyViewportResize()
    {
        if (WindowState == WindowState.Minimized || _session is null || _terminalBuffer is null ||
            _activeTab is null || TerminalTextBox.ViewportWidth <= 0 || TerminalTextBox.ViewportHeight <= 0) return;
        var columns = GetColumns();
        var rows = GetRows();
        if (_activeTab.GridSize == (columns, rows)) return;
        // Use the settled viewport, not transient window sizes during minimize/restore.
        var snapshot = _terminalBuffer.Resize(columns, rows);
        _session.Resize(columns, rows);
        _activeTab.GridSize = (columns, rows);
        QueueRender(snapshot);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var windowProbe = GetEffectiveKey(e);
        if (windowProbe is Key.OemQuestion or Key.ImeProcessed or Key.DeadCharProcessed or Key.System)
        {
            InputDebug.Log(
                $"WINDOW KEYDOWN key={windowProbe} mods={Keyboard.Modifiers} " +
                $"focused={Keyboard.FocusedElement} inView={TerminalTextBox.IsKeyboardFocusWithin}");
        }

        var controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (controlPressed && shiftPressed && e.Key == Key.T)
        {
            CreateTerminalTab(_defaultProfile);
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.Tab)
        {
            SelectRelativeTab(shiftPressed ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.W)
        {
            CloseTab(_activeTab);
            e.Handled = true;
        }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        CreateTerminalTab(_defaultProfile);
    }

    private void ProfileMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenuStyle")
        };

        AddProfileMenuItem(menu, "PowerShell", TerminalProfile.PowerShell);
        AddProfileMenuItem(menu, "Command Prompt", TerminalProfile.CommandPrompt);

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void AddProfileMenuItem(
        ItemsControl menu,
        string header,
        TerminalProfile profile)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => SelectTerminalProfile(profile);
        menu.Items.Add(item);
    }

    private void SelectTerminalProfile(TerminalProfile profile)
    {
        _defaultProfile = profile;
        AppSettings.SaveTerminalProfile(profile.ToString());
        CreateTerminalTab(profile);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExportSessionMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_terminalBuffer is null)
        {
            MessageBox.Show(
                this,
                "There is no active terminal session to export.",
                "Export session",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export terminal session",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"RtlTerminal-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var snapshot = _terminalBuffer.CaptureSnapshot();
            File.WriteAllText(
                dialog.FileName,
                CreateSessionText(snapshot),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            MessageBox.Show(
                this,
                $"The session could not be exported.{Environment.NewLine}{exception.Message}",
                "Export session",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string CreateSessionText(TerminalSnapshot snapshot)
    {
        var text = new StringBuilder();

        for (var index = 0; index < snapshot.Lines.Count; index++)
        {
            var line = snapshot.Lines[index];

            if (index > 0 && !line.WrappedFromPrevious)
                text.AppendLine();

            foreach (var run in line.Runs)
                text.Append(run.Text);
        }

        text.AppendLine();
        return text.ToString();
    }

    private void LastDirectoriesMenuItem_SubmenuOpened(
        object sender,
        RoutedEventArgs e)
    {
        LastDirectoriesMenuItem.Items.Clear();

        foreach (var directory in AppSettings.LoadLastCmdDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            var item = new MenuItem
            {
                Header = new TextBlock
                {
                    Text = directory,
                    MaxWidth = 520,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FlowDirection = FlowDirection.LeftToRight
                },
                ToolTip = directory
            };
            item.Click += (_, _) => CreateTerminalTab(
                TerminalProfile.CommandPrompt,
                directory);
            LastDirectoriesMenuItem.Items.Add(item);
        }

        if (LastDirectoriesMenuItem.Items.Count == 0)
        {
            LastDirectoriesMenuItem.Items.Add(new MenuItem
            {
                Header = "No recent directories",
                IsEnabled = false
            });
        }
    }

    private void TerminalTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        if (_session is null)
        {
            InputDebug.Log("TEXTINPUT dropped: _session NULL");
            return;
        }

        var text = !string.IsNullOrEmpty(e.Text) ? e.Text : e.SystemText;
        if (string.IsNullOrEmpty(text))
        {
            InputDebug.Log(
                $"TEXTINPUT empty (Text={InputDebug.CodePoints(e.Text ?? string.Empty)} " +
                $"SystemText={InputDebug.CodePoints(e.SystemText ?? string.Empty)})");
            return;
        }

        // WPF delivers the composed character here regardless of layout, IME, or
        // third-party Persian layouts. This is the single source of truth for all
        // printable characters — no layout detection, no KeyDown handling for ? or
        // any other printable key.
        InputDebug.Log($"TEXTINPUT write {InputDebug.CodePoints(text)}");
        _session.Write(text);
        e.Handled = true;
    }

    private void TerminalTextBox_ScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if ((e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) &&
            _session is not null && _terminalBuffer is not null)
        {
            QueueViewportResize();
        }

        if (_restoringScrollPosition || TerminalTextBox.IsUpdatingScroll)
            return;

        if (Math.Abs(e.VerticalChange) < 0.01)
            return;

        _followOutput =
            e.ExtentHeight <= e.ViewportHeight ||
            e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
    }

    private void TerminalTextBox_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (IsSgrMouseTrackingEnabled() && !TerminalTextBox.HasSelection)
            return;

        e.Handled = true;
        _suppressRightMouseUp = true;
        PasteClipboard();
        TerminalTextBox.Focus();
    }

    private ContextMenu CreateTerminalContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle"), PlacementTarget = TerminalTextBox };
        var copy = new MenuItem { Header = "_Copy", IsEnabled = TerminalTextBox.HasSelection };
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuItem { Header = "_Paste", IsEnabled = _session is not null };
        paste.Click += (_, _) => PasteClipboard();
        var select = new MenuItem { Header = "Select _all" };
        select.Click += (_, _) => TerminalTextBox.SelectAll();
        menu.Items.Add(copy); menu.Items.Add(paste); menu.Items.Add(new Separator()); menu.Items.Add(select);
        return menu;
    }

    private void TerminalTextBox_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var button = GetMouseButtonCode(e.ChangedButton);

        // Full-screen UIs light up and enable their widgets from hover state
        // (opencode's tab strip, for example, only reacts once a motion event
        // has reached the cell). Send the pointer position that the click is
        // about to use before the button goes down, so the press cannot be
        // swallowed by a stale hover.
        if (button >= 0 &&
            IsSgrMouseTrackingEnabled() &&
            _lastRenderedSnapshot?.Modes.MouseTrackingMode == 1003 &&
            TryGetMouseCell(e, out var hoverX, out var hoverY))
        {
            SendSgrMouse(3 + 32, hoverX, hoverY, released: false);
            _lastReportedMouseCell = (hoverX, hoverY, 3);
        }

        if (button < 0 || !SendMouseEvent(e, button, released: false))
        {
            TerminalTextBox.Focus();
            if (e.ChangedButton == MouseButton.Left &&
                _lastRenderedSnapshot is { } snapshot &&
                _session is not null &&
                Keyboard.Modifiers == ModifierKeys.None &&
                // Full-screen apps own the screen: arrows there move their own
                // caret or scroll them instead of editing the shell prompt.
                !snapshot.Modes.AlternateScreen &&
                TryGetMouseCell(e, out var cx, out var cy))
            {
                MovePromptCursorToCell(snapshot, cx, cy);
            }
            return;
        }

        _lastReportedMouseCell = null;
        _forwardedPressButton = button;
        Mouse.Capture(TerminalTextBox);
        TerminalTextBox.Focus();
        e.Handled = true;
    }

    private void TerminalTextBox_PreviewMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && _suppressRightMouseUp)
        {
            _suppressRightMouseUp = false;
            e.Handled = true;
            return;
        }

        var button = GetMouseButtonCode(e.ChangedButton);

        // The capture taken when the press was forwarded must always be
        // undone, even when tracking ended mid-drag (TUI exit) or the pointer
        // left the grid: a leaked capture freezes the mouse for the window.
        if (_forwardedPressButton is { } forwarded)
        {
            _forwardedPressButton = null;
            SendMouseEvent(e, forwarded, released: true);
            _lastReportedMouseCell = null;
            Mouse.Capture(null);
            e.Handled = true;
            return;
        }

        if (button < 0 || !SendMouseEvent(e, button, released: true))
            return;

        _lastReportedMouseCell = null;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private void TerminalTextBox_LostMouseCapture(
        object sender,
        MouseEventArgs e)
    {
        if (_forwardedPressButton is not { } button)
            return;

        // Capture can disappear without a matching mouse-up: another window
        // takes it, the tab is switched, or the TUI dies while the button is
        // still down. Report the missing release so the program that was
        // tracking the mouse does not stay stuck in a half-drag.
        _forwardedPressButton = null;
        _lastReportedMouseCell = null;
        SendMouseEvent(e, button, released: true);
    }

    private void TerminalTextBox_PreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        var mode = _lastRenderedSnapshot?.Modes.MouseTrackingMode ?? 0;

        if (!IsSgrMouseTrackingEnabled() ||
            mode == 1000 ||
            mode == 1002 && e.LeftButton != MouseButtonState.Pressed &&
                e.MiddleButton != MouseButtonState.Pressed &&
                e.RightButton != MouseButtonState.Pressed)
        {
            return;
        }

        var button = e.LeftButton == MouseButtonState.Pressed
            ? 0
            : e.MiddleButton == MouseButtonState.Pressed
                ? 1
                : e.RightButton == MouseButtonState.Pressed
                    ? 2
                    : 3;

        if (!TryGetMouseCell(e, out var x, out var y) ||
            _lastReportedMouseCell == (x, y, button))
        {
            return;
        }

        _lastReportedMouseCell = (x, y, button);
        SendSgrMouse(button + 32, x, y, released: false);
        e.Handled = true;
    }

    private void TerminalTextBox_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (!IsSgrMouseTrackingEnabled() ||
            !TryGetMouseCell(e, out var x, out var y))
        {
            return;
        }

        SendSgrMouse(e.Delta > 0 ? 64 : 65, x, y, released: false);
        e.Handled = true;
    }

    private void TerminalTextBox_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        InputDebug.Log("FOCUS gained by terminal view");
        if (_lastRenderedSnapshot?.Modes.FocusReporting == true)
            _session?.Write("\x1b[I");
    }

    private void TerminalTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        InputDebug.Log($"FOCUS lost by terminal view (new focus: {Keyboard.FocusedElement})");
        _pendingContextMenuKey = null;
        if (_lastRenderedSnapshot?.Modes.FocusReporting == true)
            _session?.Write("\x1b[O");
    }

    private bool SendMouseEvent(
        MouseEventArgs e,
        int button,
        bool released)
    {
        if (button == 0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && TerminalTextBox.IsLinkAt(e))
            return false;
        if (!IsSgrMouseTrackingEnabled() ||
            !TryGetMouseCell(e, out var x, out var y))
        {
            MouseDebug.Log(
                $"{(released ? "UP  " : "DOWN")} NOT forwarded btn={button}" +
                $" mode={_lastRenderedSnapshot?.Modes.MouseTrackingMode} sgr={_lastRenderedSnapshot?.Modes.SgrMouse}" +
                $" selecting={TerminalTextBox.IsSelecting} modifiers={Keyboard.Modifiers}" +
                $" | {TerminalTextBox.DescribeCell(e)}");
            return false;
        }

        SendSgrMouse(button, x, y, released);
        MouseDebug.Log(
            $"{(released ? "UP  " : "DOWN")} sent btn={button} => {x},{y}" +
            $" mode={_lastRenderedSnapshot?.Modes.MouseTrackingMode}" +
            $" | {TerminalTextBox.DescribeCell(e)}");
        return true;
    }

    private void SendSgrMouse(int button, int x, int y, bool released)
    {
        if (_session is null)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            button += 4;

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            button += 8;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            button += 16;

        _session.Write($"\x1b[<{button};{x};{y}{(released ? 'm' : 'M')}");
    }

    private bool TryGetMouseCell(MouseEventArgs e, out int x, out int y) =>
        TerminalTextBox.TryGetGridCell(e, out x, out y);

    /// <summary>
    /// Clicking on the prompt walks the shell cursor to the clicked cell with
    /// arrow keys. Every key goes out in a single write: thousands of separate
    /// flushed 8-byte writes used to freeze the UI for seconds.
    /// </summary>
    private void MovePromptCursorToCell(TerminalSnapshot snapshot, int column, int row)
    {
        if (_session is null)
            return;

        var columns = GetColumns();
        // row is 1-based and viewport relative; snapshot.CursorRow is absolute.
        var clickIndex = ((row - 1) + snapshot.ScrollbackCount) * columns + (column - 1);
        var cursorIndex = snapshot.CursorRow * columns + snapshot.CursorColumn;
        var difference = clickIndex - cursorIndex;

        if (difference == 0 || Math.Abs(difference) >= 2000)
            return;

        var applicationCursorKeys = snapshot.Modes.ApplicationCursorKeys;
        var sequence = difference < 0
            ? applicationCursorKeys ? "\x1bOD" : "\x1b[D"
            : applicationCursorKeys ? "\x1bOC" : "\x1b[C";

        _session.Write(string.Concat(
            Enumerable.Repeat(sequence, Math.Abs(difference))));
    }

    private bool IsSgrMouseTrackingEnabled() =>
        !TerminalTextBox.IsSelecting &&
        (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && _lastRenderedSnapshot?.Modes is
        {
            MouseTrackingMode: > 0,
            SgrMouse: true
        };

    private static int GetMouseButtonCode(MouseButton button) =>
        button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            _ => -1
        };

    private static string Visible(string text) =>
        string.Concat(text.Select(character =>
            character < 0x20 || character == 0x7f
                ? $"\\x{(int)character:x2}"
                : character.ToString()));

    private void SmartRtlMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastRenderedSnapshot is not null)
            Render(_lastRenderedSnapshot);

        TerminalTextBox.Focus();
    }

    private void FontSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new FontSettingsWindow(
            TerminalTextBox.FontFamily.Source,
            TerminalTextBox.FontSize,
            TerminalTextBox.FontWeight,
            TerminalTextBox.FontStyle,
            _historySize)
        {
            Owner = this
        };

        if (settingsWindow.ShowDialog() != true)
            return;

        ApplyHistorySize(settingsWindow.SelectedHistorySize);
        ApplyFontSettings(settingsWindow.SelectedSettings);
        AppSettings.SaveFont(settingsWindow.SelectedSettings);
        AppSettings.SaveHistorySize(settingsWindow.SelectedHistorySize);
        TerminalTextBox.Focus();
    }

    private void ApplyHistorySize(int historySize)
    {
        _historySize = historySize;
        SaveActiveTabState();

        foreach (var tab in _tabs)
        {
            if (tab.Buffer is not { } buffer)
                continue;

            QueueRender(
                tab,
                buffer.SetMaximumScrollbackRows(historySize));
        }
    }

    private void GuideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var guideWindow = new GuideWindow
        {
            Owner = this
        };
        guideWindow.Show();
    }

    private async void CheckForUpdatesMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        // Let terminal startup finish before performing optional network I/O.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        if (!IsLoaded)
            return;

        await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress)
        {
            if (manual)
            {
                MessageBox.Show(
                    this,
                    "An update check is already in progress.",
                    "Check for updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        _updateCheckInProgress = true;
        CheckForUpdatesMenuItem.IsEnabled = false;
        var originalHeader = CheckForUpdatesMenuItem.Header;

        if (manual)
            CheckForUpdatesMenuItem.Header = "Checking for updates...";

        try
        {
            var result = await UpdateService.CheckAsync();

            if (!result.IsUpdateAvailable)
            {
                if (manual)
                {
                    MessageBox.Show(
                        this,
                        $"Rtl Terminal {result.CurrentVersion.ToString(3)} is up to date.",
                        "Check for updates",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            if (!manual && string.Equals(
                    AppSettings.LoadSkippedUpdateVersion(),
                    result.LatestTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ShowUpdateAvailable(result);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                TaskCanceledException or
                InvalidDataException or
                JsonException)
        {
            if (manual)
            {
                MessageBox.Show(
                    this,
                    "Rtl Terminal could not check GitHub for updates.\n\n" +
                    exception.Message,
                    "Check for updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            CheckForUpdatesMenuItem.Header = originalHeader;
            CheckForUpdatesMenuItem.IsEnabled = true;
            _updateCheckInProgress = false;
        }
    }

    private void ShowUpdateAvailable(UpdateCheckResult result)
    {
        var updateWindow = new UpdateAvailableWindow(
            result.CurrentVersion,
            result.LatestVersion)
        {
            Owner = this
        };

        updateWindow.ShowDialog();

        if (updateWindow.DontShowAgain)
        {
            AppSettings.SaveSkippedUpdateVersion(result.LatestTag);
        }
        else if (string.Equals(
                     AppSettings.LoadSkippedUpdateVersion(),
                     result.LatestTag,
                     StringComparison.OrdinalIgnoreCase))
        {
            AppSettings.SaveSkippedUpdateVersion(null);
        }

        if (!updateWindow.OpenUpdateRequested)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.ReleasePage.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this,
                "The update page could not be opened.\n\n" + exception.Message,
                "Rtl Terminal update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            """
            Rtl Terminal
            by Hadi

            تقدیم به همه فارسی زبانان
            """,
            "About Rtl Terminal",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ContextMenuIntegrationMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingContextMenuItem)
            return;

        try
        {
            if (ContextMenuIntegrationMenuItem.IsChecked)
                ContextMenuIntegration.Install();
            else
                ContextMenuIntegration.Uninstall();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "تغییر منوی راست‌کلیک انجام نشد." +
                Environment.NewLine +
                exception.Message,
                "RtlTerminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RefreshContextMenuIntegrationState();
            TerminalTextBox.Focus();
        }
    }

    private void PromptForContextMenuIntegration()
    {
        if (ContextMenuIntegration.HasAnsweredInitialPrompt())
            return;

        var result = MessageBox.Show(
            this,
            "آیا گزینه «Open in RtlTerminal» به منوی راست‌کلیک پوشه‌ها اضافه شود؟",
            "RtlTerminal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        try
        {
            if (result == MessageBoxResult.Yes)
                ContextMenuIntegration.Install();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "افزودن منوی راست‌کلیک انجام نشد." +
                Environment.NewLine +
                exception.Message,
                "RtlTerminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ContextMenuIntegration.MarkInitialPromptAnswered();
            RefreshContextMenuIntegrationState();
        }
    }

    private void RefreshContextMenuIntegrationState()
    {
        _updatingContextMenuItem = true;
        ContextMenuIntegrationMenuItem.IsChecked =
            ContextMenuIntegration.IsInstalled();
        ContextMenuIntegrationMenuItem.Header =
            ContextMenuIntegrationMenuItem.IsChecked
                ? "Remove _Open in RtlTerminal"
                : "Add _Open in RtlTerminal";
        _updatingContextMenuItem = false;
    }

    private static string? ResolveStartupDirectory(
        string? requestedDirectory = null)
    {
        if (TryResolveDirectory(requestedDirectory, out var directory))
            return directory;

        var arguments = Environment.GetCommandLineArgs();

        if (arguments.Length >= 2 &&
            TryResolveDirectory(arguments[1], out directory))
        {
            return directory;
        }

        return TryResolveDirectory(Environment.CurrentDirectory, out directory)
            ? directory
            : null;
    }

    private static bool TryResolveDirectory(
        string? candidate,
        out string? directory)
    {
        directory = null;

        if (string.IsNullOrWhiteSpace(candidate) ||
            !Directory.Exists(candidate))
        {
            return false;
        }

        try
        {
            directory = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static string GetProfileTitle(TerminalProfile profile) =>
        profile switch
        {
            TerminalProfile.PowerShell => "PowerShell",
            _ => "Command Prompt"
        };

    private static string GetProfileCommand(TerminalProfile profile)
    {
        var esc = '\x1b';
        return (profile switch
        {
            TerminalProfile.PowerShell =>
                $@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoExit -Command ""$env:PT_DISABLE_VT='1'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; [Console]::InputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8; function prompt {{ Write-Host \""PS $($executionContext.SessionState.Path.CurrentLocation)>\"" -NoNewline -ForegroundColor Red; return \""{esc}[32m \"" }}; $lines=@('+--------------------------------------------------------+','| RtlTerminal v{{APP_VERSION}}                           |','| by Hadi                                                |','+--------------------------------------------------------+',''); $lines | ForEach-Object {{ Write-Host $_ -ForegroundColor Red }}""",
            _ =>
                $@"C:\Windows\System32\cmd.exe /D /Q /K ""chcp 65001>nul & set PROMPT={esc}[31m$P$G{esc}[32m & echo {esc}[31m+--------------------------------------------------------+& echo ^| RtlTerminal v{{APP_VERSION}}                                     ^|& echo ^| by Hadi                                                 ^|& echo +--------------------------------------------------------+& echo."""
        }).Replace("{APP_VERSION}", AppVersion.Display);
    }

    private static TerminalProfile LoadDefaultProfile()
    {
        var savedProfile = AppSettings.LoadTerminalProfile();

        if (Enum.TryParse(savedProfile, out TerminalProfile profile) &&
            Enum.IsDefined(profile))
        {
            return profile;
        }

        return TerminalProfile.PowerShell;
    }

    private void SaveActiveTabState()
    {
        if (_activeTab is null)
            return;

        _activeTab.Session = _session;
        _activeTab.Buffer = _terminalBuffer;
        _activeTab.CancellationTokenSource = _cancellationTokenSource;
        _activeTab.PendingSnapshot = _pendingSnapshot;
        _activeTab.RenderStartQueued = _renderStartQueued;
        _activeTab.LatestQueuedRevision = _latestQueuedRevision;
        _activeTab.LastRenderedSnapshot = _lastRenderedSnapshot;
        _activeTab.Selection = TerminalTextBox.SelectionState;
_activeTab.RenderedSmartRtlEnabled = _renderedSmartRtlEnabled;
        _activeTab.FollowOutput = _followOutput;

        var scrollViewer = FindVisualChild<ScrollViewer>(TerminalTextBox);
        _activeTab.VerticalOffset = scrollViewer?.VerticalOffset ?? 0;
    }

    private void LoadTabState(TerminalTab tab)
    {
        lock (_renderLock)
        {
            _session = tab.Session;
            _terminalBuffer = tab.Buffer;
            _cancellationTokenSource = tab.CancellationTokenSource;
            _pendingSnapshot = tab.PendingSnapshot;
            _renderStartQueued = tab.RenderStartQueued;
            _latestQueuedRevision = tab.LatestQueuedRevision;
        }

        _lastRenderedSnapshot = tab.LastRenderedSnapshot;
        _renderedSmartRtlEnabled = tab.RenderedSmartRtlEnabled;
        _followOutput = tab.FollowOutput;

        TerminalTextBox.Clear();
        if (tab.LastRenderedSnapshot is { } snapshot)
        {
            TerminalTextBox.Present(snapshot, SmartRtlMenuItem.IsChecked,
                _cellWidth, _lineHeight, false, RowRtlMenuItem.IsChecked);
            TerminalTextBox.ScrollToVerticalOffset(tab.VerticalOffset);
            TerminalTextBox.SelectionState = tab.Selection;
        }
    }

    private void SelectTab(TerminalTab tab)
    {
        if (ReferenceEquals(tab, _activeTab))
            return;

        SaveActiveTabState();
        _activeTab = tab;
        LoadTabState(tab);
        RebuildTabStrip();

        // The tab was hidden while the window may have been resized; its PTY is
        // still on the old grid until we resize it here.
        ApplyViewportResize();

        if (_pendingSnapshot is not null)
            QueueRender(_pendingSnapshot);
        else if (_lastRenderedSnapshot is not null &&
            (_renderedSmartRtlEnabled != SmartRtlMenuItem.IsChecked))
        {
            Render(_lastRenderedSnapshot);
        }

        TerminalTextBox.Focus();
    }

    private void SelectRelativeTab(int direction)
    {
        if (_activeTab is null || _tabs.Count < 2)
            return;

        var currentIndex = _tabs.IndexOf(_activeTab);
        var nextIndex = (currentIndex + direction + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[nextIndex]);
    }

    private void CloseTab(TerminalTab? tab)
    {
        if (tab is null)
            return;

        var index = _tabs.IndexOf(tab);

        if (index < 0)
            return;

        if (ReferenceEquals(tab, _activeTab))
            SaveActiveTabState();

        var wasActive = ReferenceEquals(tab, _activeTab);
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            _session = null;
            _terminalBuffer = null;
            _cancellationTokenSource = null;
            _ = tab.DisposeAsync();
            Close();
            return;
        }

        if (wasActive)
        {
            _activeTab = _tabs[Math.Min(index, _tabs.Count - 1)];
            LoadTabState(_activeTab);
            ApplyViewportResize();
        }

        RebuildTabStrip();
        TerminalTextBox.Focus();
        _ = tab.DisposeAsync();
    }

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            var isActive = ReferenceEquals(tab, _activeTab);
            var isPs = tab.Profile == TerminalProfile.PowerShell;
            var accentColor = isPs ? Color.FromRgb(45, 212, 191) : Color.FromRgb(251, 146, 60);
            var accentBrush = new SolidColorBrush(accentColor);
            accentBrush.Freeze();

            var panel = new DockPanel { Height = 32 };
            var closeButton = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 0),
                Content = "×", FontSize = 14,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = $"Close {tab.Title}", Tag = tab,
                Focusable = false,
                Style = (Style)FindResource("ChromeRoundButtonStyle")
            };
            System.Windows.Automation.AutomationProperties.SetName(closeButton, $"Close {tab.Title}");
            closeButton.Click += TabCloseButton_Click;
            DockPanel.SetDock(closeButton, Dock.Right);
            panel.Children.Add(closeButton);

            var label = new DockPanel();
            var icon = new TextBlock
            {
                Text = isPs ? "›_" : ">_",
                FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = isActive ? accentBrush : new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(icon, Dock.Left);
            label.Children.Add(icon);
            label.Children.Add(new TextBlock
            {
                Text = tab.Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var selectButton = new Button
            {
                Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 0, 4, 0), ToolTip = tab.Title, Tag = tab,
                Focusable = false,
                Foreground = new SolidColorBrush(isActive
                    ? Color.FromRgb(240, 246, 252) : Color.FromRgb(139, 148, 158)),
                Style = (Style)FindResource("ChromeTabButtonStyle")
            };
            System.Windows.Automation.AutomationProperties.SetName(selectButton, tab.Title);
            selectButton.Click += TabButton_Click;
            panel.Children.Add(selectButton);

            var tabCard = new Border
            {
                Width = 200,
                Height = 36,
                Margin = new Thickness(0, 4, 4, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = isActive ? new SolidColorBrush(Color.FromRgb(33, 38, 45)) : Brushes.Transparent,
                BorderBrush = isActive ? new SolidColorBrush(Color.FromRgb(48, 54, 61)) : Brushes.Transparent,
                BorderThickness = new Thickness(1, 1, 1, 0)
            };

            var cardGrid = new Grid();
            if (isActive)
            {
                var topIndicator = new Border
                {
                    Height = 2.5,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = accentBrush,
                    CornerRadius = new CornerRadius(1.25)
                };
                cardGrid.Children.Add(topIndicator);
            }
            cardGrid.Children.Add(panel);
            tabCard.Child = cardGrid;

            tabCard.MouseEnter += (_, _) =>
            {
                if (!ReferenceEquals(tab, _activeTab))
                {
                    tabCard.Background = new SolidColorBrush(Color.FromRgb(22, 27, 34));
                    tabCard.BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61));
                }
            };
            tabCard.MouseLeave += (_, _) =>
            {
                if (!ReferenceEquals(tab, _activeTab))
                {
                    tabCard.Background = Brushes.Transparent;
                    tabCard.BorderBrush = Brushes.Transparent;
                }
            };

            TabStrip.Children.Add(tabCard);
            if (isActive)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => tabCard.BringIntoView());
        }
    }

    private void TitleTabsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TabScroller.MaxWidth = Math.Max(0, e.NewSize.Width - 66);
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TerminalTab tab })
            SelectTab(tab);
    }

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button { Tag: TerminalTab tab })
            CloseTab(tab);
    }

    private void TerminalTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var probeKey = GetEffectiveKey(e);
        if (Keyboard.Modifiers != ModifierKeys.None ||
            probeKey is Key.OemQuestion or Key.ImeProcessed or Key.DeadCharProcessed or Key.System)
        {
            InputDebug.Log(
                $"KEYDOWN key={probeKey} system={e.SystemKey} ime={e.ImeProcessedKey} " +
                $"mods={Keyboard.Modifiers} session={(_session is null ? "NULL" : "ok")} " +
                $"persian={KeyboardHelper.IsPersianKeyboard()} focus={TerminalTextBox.IsKeyboardFocusWithin}");
        }

        if (GetEffectiveKey(e) == Key.Apps ||
            GetEffectiveKey(e) == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Do not transfer focus into the popup while its opening key is held:
            // ContextMenu handles Apps key-up itself and would immediately close.
            _pendingContextMenuKey = GetEffectiveKey(e);
            e.Handled = true;
            return;
        }

        if (_session is null)
        {
            InputDebug.Log($"KEYDOWN {probeKey}: _session is NULL - input swallowed");
            return;
        }

        var controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (controlPressed && shiftPressed && e.Key == Key.C)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (controlPressed && shiftPressed && e.Key == Key.V)
        {
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.C)
        {
            if (TerminalTextBox.HasSelection)
                CopySelection();
            else
                _session.Write("\x03");

            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.V)
        {
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (controlPressed && GetEffectiveKey(e) == Key.Space)
        {
            _session.Write("\0");
            e.Handled = true;
            return;
        }

        if (controlPressed && shiftPressed && e.Key == Key.A)
        {
            TerminalTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key >= Key.A && e.Key <= Key.Z)
        {
            var controlCharacter = (char)(e.Key - Key.A + 1);
            _session.Write(controlCharacter.ToString());
            e.Handled = true;
            return;
        }

        var altPressed = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var key = GetEffectiveKey(e);
        // Arrow-swap only for plain key presses; Alt-combos fall through below.
        if (!altPressed && !controlPressed && KeyboardHelper.IsPersianKeyboard())
        {
            if (key == Key.Left) key = Key.Right;
            else if (key == Key.Right) key = Key.Left;
        }

        var sequence = GetTerminalKeySequence(
            key,
            shiftPressed,
            altPressed,
            controlPressed,
            _lastRenderedSnapshot?.Modes.ApplicationCursorKeys == true);

        if (sequence is null)
        {
            if (probeKey == Key.OemQuestion)
                InputDebug.Log($"no terminal sequence for {probeKey} -> falls through to TextInput");
            return;
        }

        _session.Write(sequence);
        e.Handled = true;
    }

    private static Key GetEffectiveKey(KeyEventArgs e) =>
        e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

    private void TerminalTextBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_pendingContextMenuKey != GetEffectiveKey(e)) return;
        _pendingContextMenuKey = null;
        e.Handled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!TerminalTextBox.IsKeyboardFocusWithin) return;
            var menu = CreateTerminalContextMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.Closed += (_, _) => TerminalTextBox.Focus();
            menu.IsOpen = true;
        });
    }

    private static string? GetTerminalKeySequence(
        Key key,
        bool shift,
        bool alt,
        bool control,
        bool applicationCursorKeys)
    {
        if (key == Key.Tab)
            return shift ? "\x1b[Z" : "\t";

        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) +
            (control ? 4 : 0);
        var hasModifier = modifier > 1;
        var final = key switch
        {
            Key.Up => 'A',
            Key.Down => 'B',
            Key.Right => 'C',
            Key.Left => 'D',
            Key.Home => 'H',
            Key.End => 'F',
            _ => '\0'
        };

        if (final != '\0')
        {
            if (hasModifier)
                return $"\x1b[1;{modifier}{final}";

            return applicationCursorKeys
                ? $"\x1bO{final}"
                : $"\x1b[{final}";
        }

        var functionFinal = key switch
        {
            Key.F1 => 'P',
            Key.F2 => 'Q',
            Key.F3 => 'R',
            Key.F4 => 'S',
            _ => '\0'
        };

        if (functionFinal != '\0')
            return hasModifier
                ? $"\x1b[1;{modifier}{functionFinal}"
                : $"\x1bO{functionFinal}";

        var tildeCode = key switch
        {
            Key.Insert => 2,
            Key.Delete => 3,
            Key.PageUp => 5,
            Key.PageDown => 6,
            Key.F5 => 15,
            Key.F6 => 17,
            Key.F7 => 18,
            Key.F8 => 19,
            Key.F9 => 20,
            Key.F10 => 21,
            Key.F11 => 23,
            Key.F12 => 24,
            _ => 0
        };

        if (tildeCode != 0)
            return hasModifier
                ? $"\x1b[{tildeCode};{modifier}~"
                : $"\x1b[{tildeCode}~";

        return key switch
        {
            Key.Enter => alt ? "\x1b\r" : "\r",
            Key.Space => alt ? "\x1b " : " ",
            Key.Back => alt ? "\x1b\x7f" : "\x7f",
            Key.Escape => "\x1b",
            _ => null
        };
    }

    
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                WriteClipboardPaths(files);
            }
            e.Handled = true;
        }
    }

    private void CopySelection()
    {
        if (!TerminalTextBox.HasSelection)
            return;

        try { TerminalTextBox.CopySelection(); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Please try copying again.", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void PasteClipboard()
    {
        try { PasteClipboardCore(); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Please try pasting again.", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void PasteClipboardCore()
    {
        if (_session is null)
            return;

        IDataObject? dataObject = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                dataObject = Clipboard.GetDataObject();
                if (dataObject is not null)
                    break;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                System.Threading.Thread.Sleep(5);
            }
        }

        if (dataObject is null)
            return;

        // Fast-path: text is 99.9% of terminal pastes. Check in-memory without extra clipboard roundtrips!
        if (dataObject.GetDataPresent(DataFormats.UnicodeText, true) ||
            dataObject.GetDataPresent(DataFormats.Text, true))
        {
            var rawText = dataObject.GetData(DataFormats.UnicodeText, true) as string
                ?? dataObject.GetData(DataFormats.Text, true) as string;

            if (!string.IsNullOrEmpty(rawText))
            {
                var text = rawText
                    .Replace("\r\n", "\r")
                    .Replace("\n", "\r");

                WritePastedText(text);
                return;
            }
        }

        // File drop list: format paths for terminal
        if (dataObject.GetDataPresent(DataFormats.FileDrop, true) &&
            dataObject.GetData(DataFormats.FileDrop, true) is string[] files && files.Length > 0)
        {
            var paths = new List<string>(files.Length);
            for (var index = 0; index < files.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(files[index]))
                    paths.Add(files[index]);
            }

            if (paths.Count > 0)
            {
                WriteClipboardPaths(paths);
                return;
            }
        }

        // Image paste: save to temp and paste file path
        if (dataObject.GetDataPresent(DataFormats.Bitmap, true))
        {
            var imagePath = SaveClipboardImage();
            if (imagePath is not null)
                WriteClipboardPaths([imagePath]);
        }
    }

    private void WriteClipboardPaths(IReadOnlyList<string> paths)
    {
        if (_session is null || paths.Count == 0)
            return;

        var formattedPaths = new StringBuilder();

        foreach (var path in paths)
        {
            if (formattedPaths.Length > 0)
                formattedPaths.Append(' ');

            formattedPaths.Append(FormatClipboardPath(path));
        }

        WritePastedText(formattedPaths.ToString());
    }

    private void WritePastedText(string text)
    {
        if (_session is null || string.IsNullOrEmpty(text))
            return;

        // Inside a bracketed paste the payload must stay verbatim: translating
        // '?' into key records would paste escape sequences as text.
        if (_lastRenderedSnapshot?.Modes.BracketedPaste == true)
            _session.Write($"\x1b[200~{text}\x1b[201~", translateQuestionMark: false);
        else
            _session.Write(text);
    }

    private string FormatClipboardPath(string path)
    {
        return Path.GetFullPath(path);
    }

    private string? SaveClipboardImage()
    {
        var image = Clipboard.GetImage();

        if (image is null)
            return null;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "RtlTerminal",
            "Clipboard");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory,
            $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        _temporaryClipboardFiles.Add(path);
        return path;
    }

    private void CleanupTemporaryClipboardFiles()
    {
        foreach (var path in _temporaryClipboardFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _temporaryClipboardFiles.Clear();
    }

    private void ReadOutputLoop(TerminalTab tab)
    {
        var session = tab.Session;
        var buffer = tab.Buffer;
        var cancellationTokenSource = tab.CancellationTokenSource;

        if (session is null ||
            buffer is null ||
            cancellationTokenSource is null)
        {
            return;
        }

        var bytes = new byte[8192];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var cancellationToken = cancellationTokenSource.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            int byteCount;

            try
            {
                byteCount = session.Read(bytes);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                break;
            }

            if (byteCount <= 0)
                break;

            var characterCount = decoder.GetChars(
                bytes,
                0,
                byteCount,
                characters,
                0,
                flush: false);

            var output = new string(characters, 0, characterCount);

            TerminalSnapshot snapshot;
            try
            {
                snapshot = buffer.Process(output);
            }
            catch (Exception exception)
            {
                // A parser bug must not silently kill the read loop (that used
                // to leave the tab frozen with no output and no echo).
                InputDebug.Log($"OUTPUT PARSE FAILED: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            foreach (var response in snapshot.Responses)
            {
                // Capability handshakes decide whether a TUI turns on mouse
                // reporting at all, so keep the exact replies on record.
                if (response.Contains("$y") || response.EndsWith("c") || response.Contains("+r"))
                    MouseDebug.Log($"reply {Visible(response)}");
                session.Write(response);
            }

            if (snapshot.Modes.SynchronizedOutput)
                SuspendRendering(tab, snapshot.Revision);
            else
                QueueRender(tab, snapshot);
        }

        if (!cancellationToken.IsCancellationRequested)
            NotifyProcessExited(tab);
    }

    private void NotifyProcessExited(TerminalTab tab)
    {
        var buffer = tab.Buffer;

        if (buffer is null)
            return;

        try
        {
            var snapshot = buffer.Process(
                "\r\n\x1b[90m[process exited]\x1b[0m\r\n");
            QueueRender(tab, snapshot);
        }
        catch (Exception exception)
        {
            InputDebug.Log($"EXIT NOTICE FAILED: {exception.Message}");
        }
    }

    private void SuspendRendering(TerminalTab tab, long revision)
    {
        lock (_renderLock)
        {
            tab.LatestQueuedRevision = Math.Max(
                tab.LatestQueuedRevision,
                revision);
            tab.PendingSnapshot = null;

            if (!ReferenceEquals(tab, _activeTab))
                return;

            _latestQueuedRevision = tab.LatestQueuedRevision;
            _pendingSnapshot = null;
        }
    }

    private void QueueRender(TerminalSnapshot snapshot)
    {
        if (_activeTab is not null)
            QueueRender(_activeTab, snapshot);
    }

    private void QueueRender(TerminalTab tab, TerminalSnapshot snapshot)
    {
        lock (_renderLock)
        {
            if (snapshot.Revision < tab.LatestQueuedRevision)
                return;

            tab.LatestQueuedRevision = snapshot.Revision;
            tab.PendingSnapshot = snapshot;

            if (!ReferenceEquals(tab, _activeTab))
                return;

            _latestQueuedRevision = tab.LatestQueuedRevision;
            _pendingSnapshot = tab.PendingSnapshot;

            if (_renderStartQueued)
                return;

            _renderStartQueued = true;
            tab.RenderStartQueued = true;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            ProcessQueuedRender);
    }

    private void ProcessQueuedRender()
    {
        TerminalSnapshot? snapshot;
        lock (_renderLock)
        {
            _renderStartQueued = false;
            if (_activeTab is not null)
                _activeTab.RenderStartQueued = false;

            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;

            if (_activeTab is not null)
                _activeTab.PendingSnapshot = null;
        }

        if (snapshot is not null)
            Render(snapshot);

        lock (_renderLock)
        {
            if (_pendingSnapshot is not null && !_renderStartQueued)
            {
                _renderStartQueued = true;
                if (_activeTab is not null)
                    _activeTab.RenderStartQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, ProcessQueuedRender);
            }
        }
    }

    private void Render(TerminalSnapshot snapshot)
    {
        var previousModes = _lastRenderedSnapshot?.Modes;
        if (previousModes is { } modes &&
            (modes.MouseTrackingMode != snapshot.Modes.MouseTrackingMode ||
             modes.SgrMouse != snapshot.Modes.SgrMouse ||
             modes.AlternateScreen != snapshot.Modes.AlternateScreen))
        {
            MouseDebug.Log(
                $"modes tracking {modes.MouseTrackingMode}->{snapshot.Modes.MouseTrackingMode}" +
                $" sgr {modes.SgrMouse}->{snapshot.Modes.SgrMouse}" +
                $" alt {modes.AlternateScreen}->{snapshot.Modes.AlternateScreen}");
        }
        else if (previousModes is null)
        {
            MouseDebug.Log(
                $"modes start tracking {snapshot.Modes.MouseTrackingMode}" +
                $" sgr {snapshot.Modes.SgrMouse} alt {snapshot.Modes.AlternateScreen}");
        }

        _lastRenderedSnapshot = snapshot;
        _renderedSmartRtlEnabled = SmartRtlMenuItem.IsChecked;
        _restoringScrollPosition = true;
        TerminalTextBox.Present(snapshot, SmartRtlMenuItem.IsChecked,
            _cellWidth, _lineHeight, _followOutput, RowRtlMenuItem.IsChecked);
        _restoringScrollPosition = false;
    }

    private void ApplySavedFontSettings()
    {
        var settings = AppSettings.LoadFont();

        if (settings is { } savedSettings)
            ApplyFontSettings(savedSettings);
    }

    private void ApplyFontSettings(TerminalFontSettings settings)
    {
        TerminalTextBox.FontFamily = new FontFamily(settings.Family);
        TerminalTextBox.FontSize = settings.Size;
        TerminalTextBox.FontWeight = settings.Bold
            ? FontWeights.Bold
            : FontWeights.Normal;
        TerminalTextBox.FontStyle = settings.Italic
            ? FontStyles.Italic
            : FontStyles.Normal;
        UpdateFontMetrics();


        if (_session is not null && _terminalBuffer is not null)
        {
            QueueViewportResize();
            if (_lastRenderedSnapshot is not null) Render(_lastRenderedSnapshot);
            return;
        }

        if (_lastRenderedSnapshot is not null)
            Render(_lastRenderedSnapshot);
    }

    private void UpdateFontMetrics()
    {
        var typeface = new Typeface(
            TerminalTextBox.FontFamily,
            TerminalTextBox.FontStyle,
            TerminalTextBox.FontWeight,
            TerminalTextBox.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var measurement = new FormattedText(
            "M",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalTextBox.FontSize,
            Brushes.White,
            pixelsPerDip);

        _cellWidth = Math.Ceiling(Math.Max(4, measurement.WidthIncludingTrailingWhitespace) * pixelsPerDip) / pixelsPerDip;
        _lineHeight = Math.Ceiling(Math.Max(
            TerminalTextBox.FontSize + 3,
            measurement.Height + 2) * pixelsPerDip) / pixelsPerDip;
    }

    private short GetColumns()
    {
        var width = TerminalTextBox.ViewportWidth > 0
            ? TerminalTextBox.ViewportWidth
            : TerminalTextBox.ActualWidth - TerminalTextBox.Padding.Left - TerminalTextBox.Padding.Right - 18;
        return (short)Math.Clamp((int)Math.Floor(Math.Max(80, width) / _cellWidth), 10, 300);
    }

    private short GetRows()
    {
        var height = TerminalTextBox.ViewportHeight > 0
            ? TerminalTextBox.ViewportHeight
            : TerminalTextBox.ActualHeight - TerminalTextBox.Padding.Top - TerminalTextBox.Padding.Bottom;
        return (short)Math.Clamp((int)Math.Floor(Math.Max(120, height) / _lineHeight), 10, 100);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is T match)
                return match;

            var descendant = FindVisualChild<T>(child);

            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private enum TerminalProfile
    {
        PowerShell,
        CommandPrompt
    }

    private sealed class TerminalTab(
        int number,
        TerminalProfile profile,
        string profileTitle)
    {
        private int _disposeStarted;

        public int Number { get; } = number;
        public TerminalProfile Profile { get; } = profile;
        public string Title { get; } = $"{profileTitle} {number}";
        public ConPtySession? Session { get; set; }
        public TerminalBuffer? Buffer { get; set; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        public TerminalSnapshot? PendingSnapshot { get; set; }
        public bool RenderStartQueued { get; set; }
        public long LatestQueuedRevision { get; set; }
        public TerminalSnapshot? LastRenderedSnapshot { get; set; }
        public ((int Row, int Offset)? Anchor, (int Row, int Offset)? End) Selection { get; set; }
        public bool RenderedSmartRtlEnabled { get; set; } = true;
        public bool FollowOutput { get; set; } = true;
        public (short Columns, short Rows) GridSize { get; set; }
        public double VerticalOffset { get; set; }

        public Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return Task.CompletedTask;

            var session = Session;
            var cancellationTokenSource = CancellationTokenSource;
            CancellationTokenSource = null;
            Session = null;

            return Task.Run(() =>
            {
                try
                {
                    // Keep ReadOutputLoop draining until ClosePseudoConsole
                    // completes and closes the output channel.
                    session?.Dispose();
                }
                finally
                {
                    cancellationTokenSource?.Cancel();
                    cancellationTokenSource?.Dispose();
                }
            });
        }
    }
}
