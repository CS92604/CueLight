using Assistant.Core;

namespace Assistant.App.Platform;

/// <summary>
/// Notices when the app died while starting up (typically a graphics driver crashing on the first
/// frame) and, from then on, draws with the CPU instead. A crash like that can't be caught in code,
/// so it is detected the next time: a marker file is written at launch and removed once the window
/// has been up for a couple of seconds.
/// </summary>
public sealed class StartupGuard
{
    private readonly string _starting;
    private readonly string _software;

    public StartupGuard(string? directory = null)
    {
        var dir = directory ?? AppPaths.DataDirectory;
        _starting = Path.Combine(dir, "starting.flag");
        _software = Path.Combine(dir, "software-rendering.flag");
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(_starting) && !File.Exists(_software))
            {
                AppLog.Warn("The last start didn't finish; switching to software rendering from now on.");
                File.WriteAllText(_software, "Delete this file to use the graphics card again.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Draw with the CPU: asked for with a flag, or needed after a failed start.</summary>
    public bool UseSoftwareRendering => File.Exists(_software);

    public void Begin()
    {
        try { File.WriteAllText(_starting, DateTime.Now.ToString("o")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Succeeded()
    {
        try { File.Delete(_starting); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
