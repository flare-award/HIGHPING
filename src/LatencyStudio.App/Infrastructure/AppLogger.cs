using System.Globalization;

namespace LatencyStudio.App.Infrastructure;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LatencyStudio",
        "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");
    private const long MaximumLogBytes = 2 * 1024 * 1024;

    public static string FilePath => LogPath;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static IReadOnlyList<string> ReadRecentLines(int maximumLines = 80)
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(LogPath))
                {
                    return Array.Empty<string>();
                }

                return File.ReadLines(LogPath).TakeLast(Math.Clamp(maximumLines, 1, 500)).ToArray();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
                {
                    var previous = LogPath + ".1";
                    File.Delete(previous);
                    File.Move(LogPath, previous);
                }

                var line = $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} [{level}] {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must not crash or interrupt packet forwarding.
            }
        }
    }
}
