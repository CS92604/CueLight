using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Assistant.App.Platform;
using Assistant.App.ViewModels;
using Assistant.App.Views;
using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NAudio.Wave;

namespace Assistant.App;

/// <summary>
/// <c>ClaudeLiveAssistant.exe --self-test --out &lt;folder&gt; [--models &lt;folder&gt;] [--wav file --expect word] [--noavx]</c>
///
/// Checks, on the PC it runs on, the parts that differ between Windows machines: the speech engine's
/// native libraries (and, with <c>--noavx</c>, the build for CPUs without AVX2), real speech turned
/// into text, the sound devices, screen capture, drawing the windows, and hiding them from capture.
/// It writes <c>self-test.txt</c> and screenshots to the folder and exits 0 only if the essentials
/// work. Used by the Windows build to prove a release works; also handy when something is wrong on
/// someone's PC. It never contacts Claude and sends nothing anywhere (except the one-time speech
/// model download, as in normal use).
/// </summary>
internal static class SelfTest
{
    public static bool Active { get; private set; }
    private static string _out = "";
    private static readonly StringBuilder Report = new();
    private static int _failures;

    public static int Run(string[] args)
    {
        Active = true;
        _out = Arg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "claude-live-selftest");
        Directory.CreateDirectory(_out);
        SpeechModel.FolderOverride = Arg(args, "--models") ?? Path.Combine(_out, "models");
        AppLog.Directory = Path.Combine(_out, "logs");

        Line($"Claude Live Assistant self-test · {DateTime.Now:s}");
        Line($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) · {Environment.ProcessorCount} logical CPUs · "
            + $"AVX {System.Runtime.Intrinsics.X86.Avx.IsSupported}, AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}, FMA {System.Runtime.Intrinsics.X86.Fma.IsSupported} · {RuntimeInformation.FrameworkDescription}");

        if (args.Contains("--noavx"))
        {
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = new() { Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx };
            Line("Forcing the build for CPUs without AVX.");
        }

        Step("speech engine", () => Speech(Arg(args, "--wav"), Arg(args, "--expect")), essential: true);
        Step("sound devices", Audio, essential: false);
        Step("screen capture", ScreenCapture, essential: false);

        int ui = 0;
        try { ui = Program.BuildAvaloniaApp(softwareRendering: true).StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { Fail("window system", ex); }

        Line(_failures == 0 && ui == 0 ? "RESULT: all essential checks passed" : $"RESULT: {_failures + (ui == 0 ? 0 : 1)} essential check(s) failed");
        Save();
        return _failures == 0 && ui == 0 ? 0 : 1;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Line(string text) { lock (Report) Report.AppendLine(text); }
    private static void Save() { lock (Report) File.WriteAllText(Path.Combine(_out, "self-test.txt"), Report.ToString()); }

    private static void Fail(string what, Exception ex)
    {
        _failures++;
        AppLog.Error($"self-test: {what}", ex);
        Line($"FAIL  {what}: {ex.GetType().Name}: {ex.Message}");
        Save();
    }

    private static void Step(string name, Action action, bool essential)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (essential) Fail(name, ex);
            else Line($"note  {name}: {ex.GetType().Name}: {ex.Message}");
        }
        Save();
    }

    // -- the parts ------------------------------------------------------------------------------

    private static void Speech(string? wav, string? expect)
    {
        var path = SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None).GetAwaiter().GetResult();
        Line($"ok    speech model present ({new FileInfo(path).Length / 1_000_000} MB)");
        using var stt = new WhisperSpeechToText(path);
        Line($"ok    speech engine loaded: {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary}");

        float[] audio = wav is not null && File.Exists(wav) ? ReadWav(wav) : Tone();
        var text = stt.TranscribeAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
        Line($"ok    transcribed {audio.Length / 16000.0:0.0} s of audio: \"{TranscriptCleaner.Clean(text)}\"");
        if (expect is not null && !text.Contains(expect, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"expected the transcript to contain \"{expect}\"");
    }

    /// <summary>A WAV file as mono 16 kHz samples, padded with a little silence like a real recording.</summary>
    private static float[] ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var resampler = new StreamResampler(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        var all = new List<float>(new float[8000]);
        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 4];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(resampler.Process(buffer.AsSpan(0, n)));
        all.AddRange(new float[16000]);
        return all.ToArray();
    }

    private static float[] Tone() =>
        Enumerable.Range(0, 32000).Select(i => 0.1f * MathF.Sin(2 * MathF.PI * 220 * i / 16000f)).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Audio()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var flow in new[] { NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DataFlow.Capture })
        {
            var devices = enumerator.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active);
            Line($"note  {flow} devices: {(devices.Count == 0 ? "none" : string.Join("; ", devices.Select(d => d.FriendlyName)))}");
        }

        // What the app itself reports about the speakers (a friendly message if there are none).
        using var source = new WasapiAudioSource(loopback: true);
        string? message = null;
        source.Failed += m => message = m;
        source.Start();
        Thread.Sleep(2500);
        Line(message is null ? "ok    listening to the speakers started" : $"note  the app says: {message}");
    }

    private static void ScreenCapture()
    {
        var (bgra, w, h) = new GdiScreenCapture().GrabBgra(new Region(0, 0, 64, 64));
        bool black = bgra.Where((b, i) => i % 4 != 3).All(b => b == 0);
        Line($"{(black ? "note" : "ok  ")}  screen capture returned {w}×{h}{(black ? " (all black: no desktop attached?)" : "")}");
    }

    // -- the windows ----------------------------------------------------------------------------

    /// <summary>Runs inside the app: shows the real windows, saves what they look like, checks them.</summary>
    public static async Task RunWindows(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // The welcome screen.
            var welcome = new OnboardingWindow { DataContext = new KeyEntryViewModel() };
            desktop.MainWindow = welcome;
            welcome.Show();
            await Snap(welcome, "welcome");

            // The main window, with a canned reply instead of a call to Claude.
            var settings = new Settings { TypeEnabled = true };
            var engine = new Engine(new EngineOptions { Debounce = TimeSpan.FromMilliseconds(20) }, new DemoSuggester(), new UnsupportedScreenCapture());
            engine.SetTextEnabled(true);
            engine.Start();
            var vm = new MainViewModel(engine, settings, _ => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, () => { });
            var main = new MainWindow { DataContext = vm };
            desktop.MainWindow = main;
            main.Show();
            welcome.Close();
            vm.SetListening();
            engine.AddTurn(Speaker.Them, "Can you start on Monday morning, and do you have any questions about the role?");
            for (int i = 0; i < 100 && !(vm.HasSections && vm.Status == StatusKind.Listening); i++) await Task.Delay(50);
            if (!vm.HasSections) throw new InvalidOperationException("the main window never showed suggestions");
            await Snap(main, "main");

            // Hiding from screen capture, checked with the Windows API that reports it.
            if (CaptureShield.IsSupported)
            {
                if (!CaptureShield.SetHidden(true)) throw new InvalidOperationException("Windows refused to hide the window from capture");
                var handle = main.TryPlatformHandle();
                if (!GetWindowDisplayAffinity(handle, out var affinity) || affinity != 0x11)
                    throw new InvalidOperationException($"the window's capture affinity is {affinity:X}, expected 11");
                Line("ok    the window is hidden from screen capture (WDA_EXCLUDEFROMCAPTURE)");
                CaptureShield.SetHidden(false);
            }
            else Line("note  hiding from capture is not available on this Windows version");

            // Settings.
            var entry = new KeyEntryViewModel();
            var win = new SettingsWindow { DataContext = new SettingsViewModel(settings, () => { }, entry, "sk-ant-api03-abcdef1234", _ => { }) };
            win.Show(main);
            await Snap(win, "settings");

            win.Close();
            vm.Dispose();
            engine.Dispose();
        }
        catch (Exception ex) { Fail("windows", ex); }
        desktop.Shutdown(_failures == 0 ? 0 : 1);
    }

    private static IntPtr TryPlatformHandle(this Window w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private static async Task Snap(Window window, string name)
    {
        await Task.Delay(900); // let it lay out and draw
        var scale = window.RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)(window.Bounds.Width * scale)), Math.Max(1, (int)(window.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        var file = Path.Combine(_out, name + ".png");
        bitmap.Save(file);
        int colours = DistinctColours(file);
        if (colours < 8) throw new InvalidOperationException($"the {name} window drew blank ({colours} colours)");
        Line($"ok    {name} window drawn ({size.Width}×{size.Height}, {colours}+ colours)");
    }

    private static int DistinctColours(string png)
    {
        if (!OperatingSystem.IsWindows()) return 99;
        using var bmp = new System.Drawing.Bitmap(png);
        var seen = new HashSet<int>();
        for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 40))
            for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 40))
                seen.Add(bmp.GetPixel(x, y).ToArgb());
        return seen.Count;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    /// <summary>Stands in for Claude so the main window can be drawn with suggestions, offline.</summary>
    private sealed class DemoSuggester : ISuggester
    {
        public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            const string text = "SAY\n• Yes, Monday morning works for me.\n• Monday's good. Should I come to the main office first?\nTYPE\n• Monday works, see you then! Do you want me to bring anything?\n";
            for (int i = 0; i < text.Length; i += 12)
            {
                yield return text.Substring(i, Math.Min(12, text.Length - i));
                await Task.Delay(5, ct);
            }
        }
    }
}
