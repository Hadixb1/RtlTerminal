using System;
using System.IO;

namespace RtlTerminal;

/// <summary>
/// Always-on, very small log of forwarded mouse clicks. Full-screen programs
/// report "clicking a widget does nothing" while the bytes we write look fine,
/// so the only way to separate a terminal bug from an application bug is to
/// record what was sent, at which cell, and what that cell contained.
/// Only presses and releases are recorded (a few lines per click), and the file
/// rotates at 256 KB.
/// </summary>
public static class MouseDebug
{
    private static readonly object Gate = new();
    private static bool _disabled;

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RtlTerminal", "mouse_debug.log");

    public static void Log(string message)
    {
        if (_disabled) return;
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 256_000)
                {
                    var rotated = path + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(path, rotated);
                }

                File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            _disabled = true;
        }
    }
}
