using Microsoft.Win32.SafeHandles;

using System;

using System.ComponentModel;

using System.IO;

using System.Runtime.InteropServices;

using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;



namespace RtlTerminal;



public sealed class ConPtySession : IDisposable

{

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;



    private IntPtr _pseudoConsole;

    private IntPtr _attributeList;

    private SafeFileHandle? _inputWriterHandle;

    private SafeFileHandle? _outputReaderHandle;

    private FileStream? _inputWriter;

    private FileStream? _outputReader;

    private readonly object _inputLock = new();

    /// <summary>
    /// Input is handed to a dedicated writer thread instead of being written on
    /// the caller's thread. A synchronous write to the ConPTY input pipe can
    /// block until the child drains it, and when the caller is the UI thread
    /// that freezes the window; worse, the output reader replies to capability
    /// queries through the same path, which deadlocks three ways (UI waits for
    /// the pipe, ConPTY waits for the reader, the reader waits for the lock).
    /// Enqueue-and-return keeps every caller non-blocking, and one reader on one
    /// channel keeps byte order identical to the old synchronous writes.
    /// </summary>
    private readonly Channel<byte[]> _inputQueue =
        Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });

    private Task? _inputPump;

    private PROCESS_INFORMATION _processInfo;

    private bool _disposed;



    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    public ConPtySession(short columns, short rows)

    {
        // 256 KB pipe buffer prevents synchronous write deadlock and UI stalls
        // on large pastes or rapid bursts (default 0 allocates only 4 KB).
        const uint pipeBufferSize = 256 * 1024;
        CreatePipe(out var inputReadSide, out var inputWriteSide, IntPtr.Zero, pipeBufferSize);

        CreatePipe(out var outputReadSide, out var outputWriteSide, IntPtr.Zero, pipeBufferSize);



        _inputWriterHandle = new SafeFileHandle(inputWriteSide, ownsHandle: true);

        _outputReaderHandle = new SafeFileHandle(outputReadSide, ownsHandle: true);



        var hr = CreatePseudoConsole(new COORD(columns, rows), inputReadSide, outputWriteSide, 0, out _pseudoConsole);



        CloseHandle(inputReadSide);

        CloseHandle(outputWriteSide);



        if (hr != 0)

            Marshal.ThrowExceptionForHR(hr);



        _inputWriter = new FileStream(_inputWriterHandle, FileAccess.Write, 4096, isAsync: false);

        // A larger read buffer matches the 8 KB read loop and the resized pipe.
        _outputReader = new FileStream(_outputReaderHandle, FileAccess.Read, 8192, isAsync: false);

    }



    public void Start(string commandLine, string? workingDirectory = null)

    {

        // Force UTF-8 (CP 65001) as the console input code page for every child
        // session. prompt_toolkit-based programs (Hermes) read raw VT bytes when
        // ENABLE_VIRTUAL_TERMINAL_INPUT is on, and the conhost encodes those bytes
        // with the console's INPUT code page. At the default CP 437 the Persian
        // question mark U+061F has no mapping and arrives as '?' (0x3F) — or is
        // dropped entirely. Measured on a real ConPTY: bytes D8 9F -> 3F under
        // CP 437, intact under CP 65001. The old in-app fix (AttachConsole +
        // SetConsoleCP from the GUI process) never ran: AttachConsole to a
        // ConPTY-owned console fails with ERROR_GEN_FAILURE (gle=31). Setting the
        // code page from INSIDE the child chain works: wrap the shell command in
        // cmd.exe /d /c "chcp 65001>nul & <original>".
        // EXCEPTION — the PowerShell profile: its command string is itself full of
        // double quotes (-Command "...", Write-Host "PS ...>") and cmd.exe cannot
        // nest quotes. After cmd strips the outer quotes, > and | inside the PS
        // string become cmd redirections -> "The system cannot find the path
        // specified." and the shell never opens. The PS command already sets
        // [Console]::Input/OutputEncoding=UTF8 (which is SetConsoleCP from inside
        // the child) so the wrapper is redundant there.
        if (!commandLine.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
            !commandLine.Contains("chcp 65001", StringComparison.OrdinalIgnoreCase))
        {
            commandLine = $"cmd.exe /d /c \"chcp 65001>nul & {commandLine}\"";
        }

        var startupInfo = new STARTUPINFOEX();

        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();



        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref varSize);

        _attributeList = Marshal.AllocHGlobal(varSize);



        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref varSize))

            throw new Win32Exception(Marshal.GetLastWin32Error());



        if (!UpdateProcThreadAttribute(

                _attributeList,

                0,

                (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,

                _pseudoConsole,

                (IntPtr)IntPtr.Size,

                IntPtr.Zero,

                IntPtr.Zero))

        {

            throw new Win32Exception(Marshal.GetLastWin32Error());

        }



        startupInfo.lpAttributeList = _attributeList;

        // Per Microsoft's ConPTY guidance: when spawning a child under a pseudoconsole,
        // set STARTF_USESTDHANDLES with INVALID_HANDLE_VALUE so the child does NOT inherit
        // the parent's (inherited, wrong) std handles. The console subsystem then gives the
        // child proper console std handles (CONIN$/CONOUT$), which is what console apps
        // (cmd, prompt_toolkit via GetStdHandle) expect. Without this, the child's
        // GetStdHandle returns the parent's pipe handles -> ReadConsoleInputW fails with
        // ERROR_INVALID_HANDLE (gle=6, "The handle is invalid") and typed input never reaches
        // the child (Hermes bug: Persian ? and every keystroke dead; new tab shows
        // "The handle is invalid.").
        startupInfo.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
        var invalid = new IntPtr(-1);                  // INVALID_HANDLE_VALUE
        startupInfo.StartupInfo.hStdInput = invalid;
        startupInfo.StartupInfo.hStdOutput = invalid;
        startupInfo.StartupInfo.hStdError = invalid;

        var command = new StringBuilder(commandLine);



        var success = CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory,
            ref startupInfo, out _processInfo);

        if (!success)

            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Force this pseudoconsole's conhost to UTF-8 (input AND output).
        // The ConPTY conhost starts at CP 437; MSYS/bash translates console input
        // to bytes using that code page, so anything non-ASCII (Persian ? U+061F
        // and all Persian letters) arrived at programs under bash as '?' (measured:
        // U+061F -> 0x3F under CP 437, intact D8 9F under CP 65001). Setting it here,
        // once per session right after the child exists, fixes the whole chain
        // (typing AND paste of Persian text in Hermes/bash) at the source.
        // Best-effort: a failure here must never break the session.
        try
        {
            if (EnterChildConsole())
            {
                var cpBefore = GetConsoleCP();
                var outBefore = GetConsoleOutputCP();
                var cpOk = SetConsoleCP(65001);
                var outOk = SetConsoleOutputCP(65001);
                InputDebug.Log($"CONPTY-CP in={cpBefore}->65001({cpOk}) out={outBefore}->65001({outOk})");
                FreeConsole();
            }
            else
            {
                InputDebug.Log("CONPTY-CP skipped: AttachConsole failed");
            }
        }
        catch (Exception ex)
        {
            InputDebug.Log($"CONPTY-CP exception: {ex.Message}");
        }

        // Start draining input only after the child exists: the writer thread
        // blocks on the channel, so it costs nothing until the first keystroke.
        _inputPump = Task.Factory.StartNew(
            PumpInputQueue,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    }

    public int Read(byte[] buffer)

    {

        if (_outputReader is null)

            return 0;



        return _outputReader.Read(buffer, 0, buffer.Length);

    }



    private static readonly bool SupportsWin32InputMode =
        Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// Writes keyboard input to the child.
    /// <paramref name="translateQuestionMark"/> rewrites ASCII '?' and Persian '؟'
    /// into win32-input-mode key records (Windows 11 only). Callers that are
    /// replaying a paste must pass false: inside a bracketed paste those escape
    /// sequences would become part of the pasted text.
    /// </summary>
    public void Write(string text, bool translateQuestionMark = true)
    {
        if (_disposed)
        {
            InputDebug.Log($"WRITE DROPPED (disposed) {InputDebug.CodePoints(text)}");
            return;
        }

        // ASCII '?' and Persian '؟' through the ConPTY pipe:
        // On Windows 11 (Build >= 22000), ConPTY natively supports the standard win32-input-mode escape
        // sequence (\x1b[Vk;Sc;Uc;Kd;Cs;Rc_) which delivers true KEY-DOWN records directly to the child's
        // console input buffer (fixing prompt_toolkit/Hermes question mark drops).
        // On Windows 10, ConPTY does NOT support win32-input-mode; sending \x1b[..._ there causes
        // conhost to drop the sequence, so on Windows 10 we pass standard UTF-8 characters through.
        if (translateQuestionMark &&
            SupportsWin32InputMode &&
            text.AsSpan().IndexOfAny('?', '\u061f') >= 0)
        {
            text = TranslateQuestionMarks(text);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        // Enqueue only: the writer thread owns the pipe and the lock, so no
        // caller can be blocked by a slow or full ConPTY input pipe.
        if (!_inputQueue.Writer.TryWrite(bytes))
            InputDebug.Log($"WRITE DROPPED (queue closed) {InputDebug.CodePoints(text)}");

    }

    /// <summary>
    /// Drains the input channel in order. Runs on its own thread so a blocked
    /// pipe write can never stall the UI or the output reader.
    /// </summary>
    private void PumpInputQueue()
    {
        foreach (var bytes in _inputQueue.Reader.ReadAllAsync().ToBlockingEnumerable())
        {
            lock (_inputLock)
            {
                if (_inputWriter is null)
                {
                    InputDebug.Log("WRITE DROPPED (writer null, pump)");
                    return;
                }

                try
                {
                    _inputWriter.Write(bytes, 0, bytes.Length);
                    _inputWriter.Flush();
                    if (bytes.Length >= 2)
                        InputDebug.Log($"WRITE ok bytes={bytes.Length} hex={InputDebug.Hex(bytes)}");
                }
                catch (IOException exception)
                {
                    InputDebug.Log($"WRITE FAILED IOException: {exception.Message}");
                    // The child can exit between an input event and the write. A
                    // closed ConPTY pipe is a normal session condition, not an app
                    // fatal error (especially when Ctrl+C stops a TUI).
                    _inputWriter?.Dispose();
                    _inputWriter = null;
                    return;
                }
                catch (ObjectDisposedException exception)
                {
                    InputDebug.Log($"WRITE FAILED ObjectDisposed: {exception.Message}");
                    _inputWriter = null;
                    return;
                }
            }
        }
    }




    private static string TranslateQuestionMarks(string text)
    {
        const string ascii =
            "\x1b[191;53;63;1;16;1_\x1b[191;53;63;0;16;1_";
        const string persian =
            "\x1b[191;53;1567;1;16;1_\x1b[191;53;1567;0;16;1_";

        var builder = new StringBuilder(text.Length + 32);
        foreach (var character in text)
        {
            // Every occurrence is rewritten, not just a lone keystroke, so
            // pasting text that contains '?' keeps working in prompt_toolkit.
            if (character == '?')
                builder.Append(ascii);
            else if (character == '\u061f')
                builder.Append(persian);
            else
                builder.Append(character);
        }

        return builder.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    /// <summary>
    /// Attach this process to the child's console so console code page calls
    /// target it. AttachConsole is per-process; Rtl Terminal is a GUI process
    /// whose statically linked conhost never owns a console, so this succeeds on
    /// this path (measured).
    /// </summary>
    private bool EnterChildConsole()
    {
        FreeConsole();
        if (AttachConsole(_processInfo.dwProcessId))
            return true;

        InputDebug.Log($"EnterChildConsole AttachConsole failed gle={Marshal.GetLastWin32Error()}");
        return false;
    }

    public void Resize(short columns, short rows)

    {

        if (_pseudoConsole != IntPtr.Zero)

            ResizePseudoConsole(_pseudoConsole, new COORD(columns, rows));

    }



    public void Dispose()

    {

        if (_disposed)

            return;



        _disposed = true;



        // Stop accepting input, but keep the output reader alive while the
        // pseudoconsole shuts down. Older Windows builds can wait forever if
        // a client's final output is not drained concurrently.
        _inputQueue.Writer.TryComplete();

        // Close the pseudoconsole BEFORE touching the writer or its lock. A pump
        // thread can be parked inside WriteFile on a full input pipe; closing the
        // pseudoconsole tears the pipe down under it and releases that write.
        // Taking _inputLock first would deadlock the shutdown against it and the
        // window would never close. Skipped during construction failure, when no
        // child exists yet.
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        // Now the pump can finish. The writer is disposed without the lock: a
        // concurrent WriteFile either returns the teardown error or throws
        // ObjectDisposedException, both of which the pump already swallows.
        _inputWriter?.Dispose();
        _inputWriter = null;

        _outputReader?.Dispose();



        if (_processInfo.hThread != IntPtr.Zero)

            CloseHandle(_processInfo.hThread);



        if (_processInfo.hProcess != IntPtr.Zero)

            CloseHandle(_processInfo.hProcess);



        if (_attributeList != IntPtr.Zero)

        {

            DeleteProcThreadAttributeList(_attributeList);

            Marshal.FreeHGlobal(_attributeList);

        }



    }



    private static IntPtr varSize = IntPtr.Zero;



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool CloseHandle(IntPtr hObject);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern void ClosePseudoConsole(IntPtr hPC);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern bool UpdateProcThreadAttribute(

        IntPtr lpAttributeList,

        uint dwFlags,

        IntPtr attribute,

        IntPtr lpValue,

        IntPtr cbSize,

        IntPtr lpPreviousValue,

        IntPtr lpReturnSize);



    [DllImport("kernel32.dll", SetLastError = true)]

    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);



    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]

    private static extern bool CreateProcess(

        string? lpApplicationName,

        StringBuilder lpCommandLine,

        IntPtr lpProcessAttributes,

        IntPtr lpThreadAttributes,

        bool bInheritHandles,

        uint dwCreationFlags,

        IntPtr lpEnvironment,

        string? lpCurrentDirectory,

        ref STARTUPINFOEX lpStartupInfo,

        out PROCESS_INFORMATION lpProcessInformation);



    [StructLayout(LayoutKind.Sequential)]

    private readonly struct COORD

    {

        public readonly short X;

        public readonly short Y;



        public COORD(short x, short y)

        {

            X = x;

            Y = y;

        }

    }



    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]

    private struct STARTUPINFO

    {

        public int cb;

        public string? lpReserved;

        public string? lpDesktop;

        public string? lpTitle;

        public int dwX;

        public int dwY;

        public int dwXSize;

        public int dwYSize;

        public int dwXCountChars;

        public int dwYCountChars;

        public int dwFillAttribute;

        public int dwFlags;

        public short wShowWindow;

        public short cbReserved2;

        public IntPtr lpReserved2;

        public IntPtr hStdInput;

        public IntPtr hStdOutput;

        public IntPtr hStdError;

    }



    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]

    private struct STARTUPINFOEX

    {

        public STARTUPINFO StartupInfo;

        public IntPtr lpAttributeList;

    }



    [StructLayout(LayoutKind.Sequential)]

    private struct PROCESS_INFORMATION

    {

        public IntPtr hProcess;

        public IntPtr hThread;

        public int dwProcessId;

        public int dwThreadId;

    }

}
