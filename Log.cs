using System;
using System.IO;

namespace SlimPenHaptics;

// Tiny diagnostic log at %AppData%\SlimPenHaptics\log.txt: connection changes and
// write failures only, so it stays small and bug reports have something to attach.
public static class Log
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlimPenHaptics", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never get in the way of the pen.
        }
    }
}
