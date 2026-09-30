import re

with open("MainWindow.xaml", "r", encoding="utf-8") as f:
    xaml = f.read()

# Add AllowDrop and events to Window
xaml = xaml.replace('PreviewKeyDown="Window_PreviewKeyDown">',
'''PreviewKeyDown="Window_PreviewKeyDown"
        AllowDrop="True"
        PreviewDragOver="Window_PreviewDragOver"
        PreviewDrop="Window_PreviewDrop">''')

with open("MainWindow.xaml", "w", encoding="utf-8") as f:
    f.write(xaml)

with open("MainWindow.xaml.cs", "r", encoding="utf-8") as f:
    cs = f.read()

# Remove the OemQuestion block
cs = re.sub(r'if \(!controlPressed &&\s*shiftPressed &&\s*GetEffectiveKey\(e\) == Key\.OemQuestion &&\s*InputLanguageManager\.Current\.CurrentInputLanguage\s*\.TwoLetterISOLanguageName == "fa"\)\s*\{\s*_session\.Write\("\u061f"\);\s*e\.Handled = true;\s*return;\s*\}', '', cs)

# Add Drag and Drop handlers
drag_handlers = """
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
"""

cs = cs.replace("private void CopySelection()", drag_handlers + "\n    private void CopySelection()")

with open("MainWindow.xaml.cs", "w", encoding="utf-8") as f:
    f.write(cs)

with open("TerminalView.cs", "r", encoding="utf-8") as f:
    tv = f.read()

# Change cursor to I-Beam (width 2)
tv = re.sub(r'var rect = new Rect\(cursor\?\.X \?\? _snapshot\.CursorColumn \* _cellWidth, y, Math\.Max\(_cellWidth, cursor\?\.Width \?\? _cellWidth\), _lineHeight\);',
            r'var rect = new Rect(cursor?.X ?? _snapshot.CursorColumn * _cellWidth, y, 2.0, _lineHeight);', tv)

with open("TerminalView.cs", "w", encoding="utf-8") as f:
    f.write(tv)

