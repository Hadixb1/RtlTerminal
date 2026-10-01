using System;
using System.Runtime.InteropServices;

namespace RtlTerminal;

public static class KeyboardHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    public static bool IsPersianKeyboard()
    {
        var layout = GetKeyboardLayout(0);
        var langId = (ushort)((uint)layout & 0xFFFF);
        return langId == 0x0429;
    }
}
