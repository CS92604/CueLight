using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Cuelight.App.Platform;
using Cuelight.App.ViewModels;
using Cuelight.App.Views;
using Cuelight.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NAudio.Wave;

namespace Cuelight.App;

/// <summary>
/// <c>Cuelight.exe --self-test --out &lt;folder&gt; [--models &lt;folder&gt;] [--wav file --expect word] [--noavx] [--require-vc]</c>
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
        _out = Arg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "cuelight-selftest");
        Directory.CreateDirectory(_out);
        SpeechModel.FolderOverride = Arg(args, "--models") ?? Path.Combine(_out, "models");
        AppLog.Directory = Path.Combine(_out, "logs");

        Line($"Cuelight self-test · {DateTime.Now:s}");
        Line($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) · {Environment.ProcessorCount} logical CPUs · "
            + $"AVX {System.Runtime.Intrinsics.X86.Avx.IsSupported}, AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}, FMA {System.Runtime.Intrinsics.X86.Fma.IsSupported} · {RuntimeInformation.FrameworkDescription}");

        if (args.Contains("--noavx"))
        {
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = new() { Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx };
            Line("Forcing the build for CPUs without AVX.");
        }

        Line($"Running as: {Environment.ProcessPath} · single file: {string.IsNullOrEmpty(typeof(SelfTest).Assembly.Location)}");
        Step("speech engine", () => Speech(Arg(args, "--wav"), Arg(args, "--expect"), args.Contains("--require-vc")), essential: true);
        Step("audio system", AudioSystem, essential: true);   // works with no sound device at all
        Step("sound devices", Audio, essential: false);       // a CI machine has none
        Step("screen capture", ScreenCapture, essential: true);

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

    private static void Speech(string? wav, string? expect, bool requireVc)
    {
        var path = SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None).GetAwaiter().GetResult();
        Line($"ok    speech model present ({new FileInfo(path).Length / 1_000_000} MB)");
        using var stt = new WhisperSpeechToText(path);
        Line($"ok    speech engine loaded: {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary}");

        // Where did each library come from? The app's own unpacked copies are what must be in use, so the
        // result doesn't depend on what else is installed on this PC.
        var modules = SpeechRuntime.LoadedModules().ToList();
        foreach (var m in modules) Line("      " + m);
        if (SpeechRuntime.IsEmbedded)
        {
            Line($"ok    speech engine unpacked to {SpeechRuntime.Folder} (Visual C++ runtime included: {SpeechRuntime.HasVisualCppRuntime})");
            if (requireVc)
            {
                if (!SpeechRuntime.HasVisualCppRuntime) throw new InvalidOperationException("the Visual C++ runtime isn't inside the app");
                var outside = modules.Where(m => !m.Contains(SpeechRuntime.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
                if (outside.Count > 0 || !modules.Any(m => m.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("some speech libraries came from outside the app's own copies: " + string.Join("; ", outside));
            }
        }

        float[] audio = wav is not null && File.Exists(wav) ? ReadWav(wav) : Tone();
        var text = stt.TranscribeAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
        Line($"ok    transcribed {audio.Length / 16000.0:0.0} s of audio: \"{TranscriptCleaner.Clean(text)}\"");
        if (expect is not null && !text.Contains(expect, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"expected the transcript to contain \"{expect}\"");

        // The quick pass behind the live words must hear the same words, and be quicker. (The first run of each
        // pays for loading things, so each is timed on its second go.)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string preview = "";
        long normalMs = 0, quickMs = 0;
        for (int run = 0; run < 2; run++)
        {
            clock.Restart();
            stt.TranscribeAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
            normalMs = clock.ElapsedMilliseconds;
            clock.Restart();
            preview = stt.PreviewAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
            quickMs = clock.ElapsedMilliseconds;
        }
        Line($"ok    live-words pass: {quickMs} ms against {normalMs} ms for the normal pass: \"{TranscriptCleaner.Clean(preview)}\"");
        if (expect is not null && !preview.Contains(expect, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"the live-words pass didn't hear \"{expect}\"");
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

    /// <summary>Talks to the Windows audio system. Needs COM, which must not be switched off; works even with no
    /// speakers or microphone, so a machine without sound hardware can still prove it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AudioSystem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        bool speakers = enumerator.HasDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        bool microphone = enumerator.HasDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
        Line($"ok    audio system reachable (speakers: {(speakers ? "yes" : "none")}, microphone: {(microphone ? "yes" : "none")})");
    }

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
        if (!OperatingSystem.IsWindows()) return;
        var (bgra, w, h) = new GdiScreenCapture().GrabBgra(new Region(0, 0, 64, 64));
        bool black = bgra.Where((b, i) => i % 4 != 3).All(b => b == 0);
        Line($"{(black ? "note" : "ok  ")}  screen capture returned {w}×{h}{(black ? " (all black: no desktop attached?)" : "")}");
        var png = new GdiScreenCapture().CapturePng(new Region(0, 0, 200, 100));
        Line($"ok    screen capture as a PNG: {png.Length} bytes");
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

            // The Copy buttons: put text on the clipboard and read it back.
            await Check("clipboard", async () =>
            {
                var clipboard = TopLevel.GetTopLevel(main)?.Clipboard ?? throw new InvalidOperationException("the window has no clipboard");
                await clipboard.SetTextAsync("cuelight-self-test");
                using var data = await clipboard.TryGetDataAsync();
                var back = data is null ? null : await data.TryGetTextAsync();
                if (back != "cuelight-self-test") throw new InvalidOperationException($"the clipboard held \"{back}\" instead");
                Line("ok    copied text to the clipboard and read it back");
            });

            // "Select area": the screen is photographed, shown dimmed in a full-screen window per monitor, and
            // Escape closes it again. This exercises screen capture end to end.
            await Check("area picker", async () =>
            {
                RegionPicker.WindowsShown = windows =>
                {
                    Line($"ok    area picker opened {windows.Count} full-screen window(s)");
                    windows[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                };
                try
                {
                    var picked = await RegionPicker.PickAsync(main, new GdiScreenCapture());
                    if (picked is not null) throw new InvalidOperationException("Escape should have cancelled the picker");
                    Line("ok    area picker photographed the screen and closed on Escape");
                }
                finally { RegionPicker.WindowsShown = null; }
            });

            // The outline around the watched area: exactly 3 pixels thick, outside the box, and never over the text
            // inside it. (It used to be drawn with tiny ordinary windows, which Windows enlarged to about 32 × 38.)
            await Check("region outline", async () =>
            {
                var area = new Region(600, 200, 300, 160);   // clear of the app's own windows on a 1024-pixel-wide screen
                const int margin = 12;
                var outline = new RegionOutline();
                try
                {
                    outline.Show(area);
                    await Task.Delay(500);
                    var (bgra, w, h) = new GdiScreenCapture().GrabBgra(new Region(area.Left - margin, area.Top - margin, area.Width + 2 * margin, area.Height + 2 * margin));
                    bool Orange(int x, int y)
                    {
                        int i = (y * w + x) * 4;
                        return Math.Abs(bgra[i] - 0x57) <= 6 && Math.Abs(bgra[i + 1] - 0x77) <= 6 && Math.Abs(bgra[i + 2] - 0xD9) <= 6;
                    }
                    int midX = margin + area.Width / 2, midY = margin + area.Height / 2;
                    int right = margin + area.Width, bottom = margin + area.Height;
                    var problems = new List<string>();
                    void Expect(bool want, string what, int x, int y) { if (Orange(x, y) != want) problems.Add($"{what} at ({x},{y}) should {(want ? "" : "not ")}be orange"); }
                    for (int d = 1; d <= RegionOutline.Thickness; d++)
                    {
                        Expect(true, "left bar", margin - d, midY);
                        Expect(true, "top bar", midX, margin - d);
                        Expect(true, "right bar", right + d - 1, midY);
                        Expect(true, "bottom bar", midX, bottom + d - 1);
                    }
                    Expect(false, "the area's first column", margin, midY);
                    Expect(false, "the area's first row", midX, margin);
                    Expect(false, "the area's top-left corner", margin, margin);
                    Expect(false, "a point 8 pixels inside", margin + 8, margin + 8);
                    Expect(false, "just past the left bar", margin - RegionOutline.Thickness - 1, midY);
                    Expect(false, "just past the top bar", midX, margin - RegionOutline.Thickness - 1);
                    if (problems.Count > 0) throw new InvalidOperationException("the outline is wrong: " + string.Join("; ", problems));
                    Line("ok    the outline is 3 pixels thick, outside the area, and leaves the area itself clear");

                    if (CaptureShield.IsSupported)
                    {
                        outline.Hide();
                        CaptureShield.SetHidden(true);
                        outline.Show(area);
                        foreach (var hwnd in outline.Handles)
                            if (!GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != 0x11)
                                throw new InvalidOperationException($"an outline bar's capture affinity is {affinity:X}, expected 11");
                        Line("ok    the outline is hidden from screen capture too");
                    }
                }
                finally
                {
                    outline.Hide();
                    CaptureShield.SetHidden(false);
                }
                await Task.Delay(300);
                var after = new GdiScreenCapture().GrabBgra(new Region(area.Left - RegionOutline.Thickness, area.Top + area.Height / 2, RegionOutline.Thickness, 1));
                if (after.Bgra[0] == 0x57 && after.Bgra[1] == 0x77 && after.Bgra[2] == 0xD9) throw new InvalidOperationException("the outline is still on screen after Hide");
                Line("ok    the outline goes away when hidden");
            });

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

    /// <summary>Runs one check inside the window run, recording a failure without stopping the rest.</summary>
    private static async Task Check(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { Fail(name, ex); }
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
