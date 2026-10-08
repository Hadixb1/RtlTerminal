using System;
using System.IO;
using System.Text;

namespace RtlTerminal;

/// <summary>
/// Lightweight input-path diagnostics used to catch "the question mark silently
/// never reaches the session" reports. Writes to
/// %LOCALAPPDATA%\RtlTerminal\input_debug.log and rotates at 4 MB.
/// </summary>
public static class InputDebug
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RtlTerminal", "input_debug.log");

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("RTLTERMINAL_DEBUG") == "1";

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                if (_writer is null)
                {
                    var path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (File.Exists(path) && new FileInfo(path).Length > 4_000_000)
                    {
                        var rotated = path + ".1";
                        if (File.Exists(rotated)) File.Delete(rotated);
                        File.Move(path, rotated);
                    }

                    // Several Rtl Terminal instances append to this file. Whoever opens it
                    // first holds it with FileShare.Read, which rejects every other writer —
                    // fall back to a per-process file instead of failing silently.
                    _writer = OpenWriter(path);
                }

                _writer.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} pid={Environment.ProcessId} {message}");
            }
        }
        catch
        {
            // Diagnostics must never break typing.
        }
    }

    private static StreamWriter OpenWriter(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        foreach (var candidate in new[] { path, Path.Combine(dir, $"input_debug.{Environment.ProcessId}.log") })
        {
            try
            {
                return new StreamWriter(
                    new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    Encoding.UTF8) { AutoFlush = true };
            }
            catch (IOException)
            {
                // held by another instance — try the per-process file
            }
        }

        return new StreamWriter(Stream.Null) { AutoFlush = true };
    }

    /// <summary>Human readable code points, e.g. "U+061F" or "U+0628,U+0631".</summary>
    public static string CodePoints(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "<empty>";

        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append("U+").Append(((int)c).ToString("X4"));
        }

        return sb.ToString();
    }

    /// <summary>First bytes of a UTF-8 payload as hex, capped for readability.</summary>
    public static string Hex(byte[] bytes, int cap = 24)
    {
        var n = Math.Min(bytes.Length, cap);
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
            sb.Append(bytes[i].ToString("X2"));
        if (bytes.Length > n) sb.Append("..");
        return sb.Length == 0 ? "<none>" : sb.ToString();
    }
}
