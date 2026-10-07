using System.Text;

namespace Assistant.Core;

/// <summary>
/// A small rolling log (about 1 MB at most) in the app's data folder, so a problem on someone's PC
/// can be diagnosed afterwards. It records errors and start-up facts about the machine, never what
/// was said, what was on screen, or the API key. Logging never throws.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    /// <summary>Where the log lives. Settable so tests can point it somewhere temporary.</summary>
    public static string Directory { get; set; } = Path.Combine(AppPaths.DataDirectory, "logs");

    public static string FilePath => Path.Combine(Directory, "app.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.Combine(Directory, "app.old.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // A full disk or a read-only profile must never take the app down.
        }
    }
}
