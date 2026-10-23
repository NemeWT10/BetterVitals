using System.Text;

namespace BetterVitals.Core;

/// <summary>Tiny local log (no telemetry): logs\app.log, rotated at 1 MB. Never throws.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private const long MaxSize = 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            if (string.IsNullOrEmpty(AppEnv.LogDir)) return;
            lock (Gate)
            {
                Directory.CreateDirectory(AppEnv.LogDir);
                var path = Path.Combine(AppEnv.LogDir, "app.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSize)
                {
                    var old = Path.Combine(AppEnv.LogDir, "app.1.log");
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
