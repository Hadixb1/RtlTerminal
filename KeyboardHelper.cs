using System;
using System.Runtime.InteropServices;

namespace RtlTerminal;

public static class KeyboardHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    public static bool IsPersianKeyboard()
    {
        try
        {
            var fg = GetForegroundWindow();
            uint tid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
            if (tid != 0)
            {
                var layoutFg = (ulong)(long)GetKeyboardLayout(tid);
                var langIdFg = (ushort)(layoutFg & 0xFFFF);
                var primaryLangFg = langIdFg & 0x03FF;
                if (primaryLangFg is 0x29 or 0x01 or 0x20)
                    return true;
            }

            var layout = (ulong)(long)GetKeyboardLayout(0);
            var langId = (ushort)(layout & 0xFFFF);
            var primaryLang = langId & 0x03FF;
            // 0x29: Persian (Farsi), 0x01: Arabic, 0x20: Urdu
            if (primaryLang is 0x29 or 0x01 or 0x20)
                return true;
        }
        catch
        {
        }

        try
        {
            var name = System.Windows.Input.InputLanguageManager.Current?.CurrentInputLanguage?.TwoLetterISOLanguageName;
            return name is "fa" or "ar" or "ur";
        }
        catch
        {
            return false;
        }
    }
}
