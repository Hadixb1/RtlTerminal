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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] System.Text.StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    /// <summary>
    /// The character the CURRENT thread's layout produces for Shift+OemQuestion: '؟' on a
    /// Persian layout, '?' on an English one. Unlike <see cref="IsPersianKeyboard"/> this
    /// never looks at GetForegroundWindow(), so a transient popup/menu with an English
    /// layout cannot make it answer "English" while the user is clearly typing Persian
    /// (the KeyDown arrived on THIS window, so this thread's layout is the ground truth).
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uType, IntPtr dwhkl);

    public static char GetShiftedOemQuestion()
    {
        try
        {
            // Ground truth = the layout of the thread that received this KeyDown.
            // GetForegroundWindow() can answer for a transient popup/menu (Alt just moved
            // focus), which is how a Persian press was judged English and '?' was sent.
            var hkl = GetKeyboardLayout(0);
            var langId = (ushort)((long)hkl & 0xFFFF);
            var primaryLang = (ushort)(langId & 0x03FF); // 0x29 Persian, 0x01 Arabic, 0x20 Urdu
            if (primaryLang is 0x29 or 0x01 or 0x20)
                return '\u061f';

            // Non-Persian layout: ask the layout itself what Shift+OemQuestion yields.
            // A scan code is required — ToUnicodeEx(0xBF, 0, ...) returns 0 and used to
            // fall through to the foreground-window heuristic above.
            var keyState = new byte[256];
            keyState[0x10] = 0x80; // VK_SHIFT
            var scan = MapVirtualKeyEx(0xBF, 0, hkl);
            var buffer = new System.Text.StringBuilder(8);
            if (ToUnicodeEx(0xBF, scan, keyState, buffer, buffer.Capacity, 0, hkl) > 0 && buffer.Length > 0)
                return buffer[0];
        }
        catch
        {
        }

        return '?';
    }

    /// <summary>
    /// The HKL of the CURRENT thread (the thread that received the KeyDown).
    /// Pass to ConPtySession.WriteKeyChar so VkKeyScanExW resolves through the layout
    /// that produced the character.
    /// </summary>
    public static IntPtr GetCurrentThreadLayout()
    {
        try { return GetKeyboardLayout(0); }
        catch { return IntPtr.Zero; }
    }

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
