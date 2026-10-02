using System;
using System.Runtime.InteropServices;

namespace RtlTerminal;

public static class KeyboardHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    public static bool IsPersianKeyboard()
    {
        try
        {
            var layout = (ulong)(long)GetKeyboardLayout(0);
            var langId = (ushort)(layout & 0xFFFF);
            var primaryLang = langId & 0x03FF;
            // 0x29: Persian (Farsi), 0x01: Arabic, 0x20: Urdu
            return primaryLang is 0x29 or 0x01 or 0x20;
        }
        catch
        {
            var name = System.Windows.Input.InputLanguageManager.Current?.CurrentInputLanguage?.TwoLetterISOLanguageName;
            return name is "fa" or "ar" or "ur";
        }
    }
}
