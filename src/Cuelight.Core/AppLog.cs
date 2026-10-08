using System.Text;
using System.Text.RegularExpressions;

namespace Cuelight.Core;

/// <summary>
/// A small rolling log (about 1 MB at most) in the app's data folder, so a problem on someone's PC
/// can be diagnosed afterwards. It records errors and start-up facts about the machine, never what
/// was said, what was on screen, or the API key (anything that looks like a key or a password is blanked out
/// before it is written, since people paste this file into bug reports). Logging never throws.
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

    /// <summary>Blanks out anything in a log line that looks like an API key, a bearer token, a key in a web
    /// address, or a password in one.</summary>
    public static string Redact(string text) => Secrets.Aggregate(text, (t, rule) => rule.Pattern.Replace(t, rule.Replacement));

    private static readonly (Regex Pattern, string Replacement)[] Secrets =
    {
        (new(@"\b(sk-ant-|sk-|xai-|nvapi-|gsk_|AIza|pplx-|hf_)[A-Za-z0-9_\-]{12,}", RegexOptions.Compiled), "$1[hidden]"),
        (new(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-]{8,}=*", RegexOptions.Compiled), "$1 [hidden]"),
        (new(@"(?i)([?&](?:key|api[_-]?key|access[_-]?token|token|secret)=)[^&\s""'>]+", RegexOptions.Compiled), "$1[hidden]"),
        (new(@"(?i)(\bhttps?://)[^/\s:@]+:[^@\s/]+@", RegexOptions.Compiled), "$1[hidden]@"),
    };

    private static void Write(string level, string message)
    {
        message = Redact(message);
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
