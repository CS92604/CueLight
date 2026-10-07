using System.Net;
using System.Runtime.InteropServices;
using Assistant.App.Platform;
using Assistant.Core;
using Avalonia;

namespace Assistant.App;

internal static class Program
{
    /// <summary>Held for the life of the process; the app asks it to bring the window forward.</summary>
    internal static SingleInstance? Single { get; private set; }

    private static StartupGuard? _guard;

    /// <summary>Marks a start as finished once the window has been up for a moment. Created on first use,
    /// so a second copy that just hands over to the first never looks at the first one's start-up marker.</summary>
    internal static StartupGuard Guard => _guard ??= new StartupGuard();

    [STAThread]
    public static int Main(string[] args)
    {
        InstallSafetyNets();
        if (args.Contains("--self-test")) return SelfTest.Run(args); // checks this PC; see SelfTest

        using var single = SingleInstance.TryAcquire();
        if (single is null) return 0; // already running: that copy was asked to come forward
        Single = single;

        var guard = Guard;
        bool software = args.Contains("--software-rendering")
            || Environment.GetEnvironmentVariable("CLAUDE_LIVE_SOFTWARE_RENDERING") == "1"
            || guard.UseSoftwareRendering;
        guard.Begin();
        LogStartup(software);

        try
        {
            return BuildAvaloniaApp(software).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("The app failed to start", ex);
            ShowFatal($"Claude Live Assistant couldn't start:\n\n{ex.Message}\n\nDetails were saved to:\n{AppLog.FilePath}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(softwareRendering: false);

    public static AppBuilder BuildAvaloniaApp(bool softwareRendering)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        if (softwareRendering && OperatingSystem.IsWindows())
            builder = builder.With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } });
        return builder;
    }

    /// <summary>Things that make the app work on more PCs, and leave a trail when it doesn't.</summary>
    private static void InstallSafetyNets()
    {
        // Work PCs often reach the internet through an authenticating proxy; use the Windows sign-in for it.
        try { HttpClient.DefaultProxy.Credentials = CredentialCache.DefaultCredentials; }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException) { }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Unhandled exception (the app is closing)", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogStartup(bool software)
    {
        try
        {
            AppLog.Info($"Start: {typeof(Program).Assembly.GetName().Version} · {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) · "
                + $"{Environment.ProcessorCount} logical CPUs · AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported} · "
                + $"{RuntimeInformation.FrameworkDescription} · {(software ? "software" : "GPU")} rendering");
        }
        catch { }
    }

    private static void ShowFatal(string message)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine(message); return; }
        MessageBoxW(IntPtr.Zero, message, "Claude Live Assistant", 0x10 /* MB_ICONERROR */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
