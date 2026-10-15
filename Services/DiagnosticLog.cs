using System;
using System.IO;

namespace Ocula.Services;

/// <summary>
/// A small text log for problems that would otherwise vanish, such as a background scan that stops.
/// It lives next to the library (%LOCALAPPDATA%\Ocula\ocula.log; for the packaged app that is under
/// LocalCache) and never leaves the PC. It holds the time, what happened and the exception, never
/// photo contents. When it passes 512 KB the old half is dropped.
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocula", "ocula.log");

    public static void Write(string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(Path);
                    File.WriteAllLines(Path, lines[(lines.Length / 2)..]);
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}" +
                                         (error == null ? string.Empty : $"{Environment.NewLine}    {error.GetType().Name}: {error.Message}{Environment.NewLine}{error.StackTrace}") +
                                         Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never be the thing that breaks the app.
        }
    }
}
