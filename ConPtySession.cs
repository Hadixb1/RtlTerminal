using Microsoft.Win32.SafeHandles;

using System;

using System.ComponentModel;

using System.IO;

using System.Runtime.InteropServices;

using System.Text;



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

        CreatePipe(out var inputReadSide, out var inputWriteSide, IntPtr.Zero, 0);

        CreatePipe(out var outputReadSide, out var outputWriteSide, IntPtr.Zero, 0);



        _inputWriterHandle = new SafeFileHandle(inputWriteSide, ownsHandle: true);

        _outputReaderHandle = new SafeFileHandle(outputReadSide, ownsHandle: true);



        var hr = CreatePseudoConsole(new COORD(columns, rows), inputReadSide, outputWriteSide, 0, out _pseudoConsole);



        CloseHandle(inputReadSide);

        CloseHandle(outputWriteSide);



        if (hr != 0)

            Marshal.ThrowExceptionForHR(hr);



        _inputWriter = new FileStream(_inputWriterHandle, FileAccess.Write, 4096, isAsync: false);

        _outputReader = new FileStream(_outputReaderHandle, FileAccess.Read, 4096, isAsync: false);

    }



    public void Start(string commandLine, string? workingDirectory = null)

    {

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



        var command = new StringBuilder(commandLine);



        var success = CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory,
            ref startupInfo, out _processInfo);

        if (!success)

            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Force the pseudo console to UTF-8 (CP 65001).
        // conhost decodes the ConPTY input pipe and converts KEY_EVENT -> bytes for
        // ReadFile() using the console's INPUT code page. On an OEM page (437) the
        // UTF-8 bytes of U+061F (؟) are not representable, so the child process
        // received '?' instead. The code page lives on the pseudo console the child
        // is attached to, so we attach to it briefly and set it there.
        FreeConsole();
        var attached = AttachConsole(_processInfo.dwProcessId);
        var cpOk = attached && SetConsoleCP(65001) && SetConsoleOutputCP(65001);
        InputDebug.Log($"CONPTY cp set attached={attached} ok={cpOk} gle={Marshal.GetLastWin32Error()} inCp={GetConsoleCP()} outCp={GetConsoleOutputCP()}");
        if (attached)
            FreeConsole();

    }

    public int Read(byte[] buffer)

    {

        if (_outputReader is null)

            return 0;



        return _outputReader.Read(buffer, 0, buffer.Length);

    }



    public void Write(string text)
    {
        if (_inputWriter is null)
        {
            InputDebug.Log($"WRITE DROPPED (writer null) {InputDebug.CodePoints(text)}");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_inputLock)
        {
            if (_inputWriter is null)
            {
                InputDebug.Log($"WRITE DROPPED (writer null, locked) {InputDebug.CodePoints(text)}");
                return;
            }
            try
            {
                _inputWriter.Write(bytes, 0, bytes.Length);
                _inputWriter.Flush();
                if (text.Contains('?') || text.Contains('\u061f'))
                    InputDebug.Log($"WRITE ok bytes={bytes.Length} hex={InputDebug.Hex(bytes)}");
            }
            catch (IOException exception)
            {
                InputDebug.Log($"WRITE FAILED IOException: {exception.Message} {InputDebug.CodePoints(text)}");
                // The child can exit between an input event and the write. A
                // closed ConPTY pipe is a normal session condition, not an app
                // fatal error (especially when Ctrl+C stops a TUI).
                _inputWriter?.Dispose();
                _inputWriter = null;
            }
            catch (ObjectDisposedException exception)
            {
                InputDebug.Log($"WRITE FAILED ObjectDisposed: {exception.Message} {InputDebug.CodePoints(text)}");
                _inputWriter = null;
            }
        }

    }




    // ---- Synthetic key-event input (Shift+/ = U+061F) --------------------------------
    // conhost turns raw UTF-8 bytes written to the ConPTY pipe into KEY_EVENT records.
    // For characters that do not map onto the layout conhost uses (U+061F among them) it
    // puts the character on the KEY-UP record, while clients that read only KEY-DOWN
    // (prompt_toolkit -> Hermes / any Python TUI) silently drop it: typing "Shift+/"
    // produced nothing. Writing the record ourselves guarantees KEY-DOWN carries it.
    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_RW = 0x00000003;
    private const uint OPEN_EXISTING_FILE = 3;
    private const uint KEY_EVENT_TYPE = 1;
    private const uint SHIFT_PRESSED = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD_NATIVE
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char uChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD_NATIVE
    {
        public ushort EventType;
        public KEY_EVENT_RECORD_NATIVE KeyEvent;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInputW(IntPtr hConsoleInput, INPUT_RECORD_NATIVE[] lpBuffer,
        uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanExW(char ch, IntPtr hkl);

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uType, IntPtr dwhkl);

    public bool WriteKeyChar(char character, bool shiftPressed)
    {
        if (_processInfo.dwProcessId == 0)
            return false;

        // AttachConsole is per-process: release any console we still hold so this write
        // targets this session's child (Rtl Terminal itself is a GUI process).
        FreeConsole();
        if (!AttachConsole(_processInfo.dwProcessId))
        {
            InputDebug.Log($"WriteKeyChar AttachConsole failed gle={Marshal.GetLastWin32Error()}");
            return false;
        }

        var handle = CreateFileW("CONIN$", GENERIC_READ_WRITE, FILE_SHARE_RW, IntPtr.Zero,
            OPEN_EXISTING_FILE, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            InputDebug.Log($"WriteKeyChar CONIN$ failed gle={Marshal.GetLastWin32Error()}");
            FreeConsole();
            return false;
        }

        var scan = VkKeyScanExW(character, IntPtr.Zero);
        var vk = (byte)(scan & 0xFF);
        var modifiers = (byte)((scan >> 8) & 0xFF);
        if (scan == -1)
        {
            vk = 0;
            modifiers = 0;
        }

        var state = 0u;
        if ((modifiers & 0x10) != 0 || (shiftPressed && (modifiers & 0xFF) == 0))
            state |= SHIFT_PRESSED;

        var records = new INPUT_RECORD_NATIVE[2];
        records[0] = new INPUT_RECORD_NATIVE
        {
            EventType = (ushort)KEY_EVENT_TYPE,
            KeyEvent = new KEY_EVENT_RECORD_NATIVE
            {
                bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = vk,
                wVirtualScanCode = (ushort)MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, IntPtr.Zero),
                uChar = character, dwControlKeyState = state,
            },
        };
        records[1] = records[0];
        records[1].KeyEvent.bKeyDown = 0;

        var ok = WriteConsoleInputW(handle, records, (uint)records.Length, out var written);
        InputDebug.Log(ok
            ? $"KEYEV write ok events={written} char=U+{(int)character:X4}"
            : $"KEYEV write failed gle={Marshal.GetLastWin32Error()}");

        CloseHandle(handle);
        FreeConsole();
        return ok && written == records.Length;
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
        lock (_inputLock)
        {
            _inputWriter?.Dispose();
            _inputWriter = null;
        }

        if (_pseudoConsole != IntPtr.Zero)

        {

            ClosePseudoConsole(_pseudoConsole);

            _pseudoConsole = IntPtr.Zero;

        }

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
