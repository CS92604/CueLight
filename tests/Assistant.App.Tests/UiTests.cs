using System.Runtime.CompilerServices;
using Assistant.App;
using Assistant.App.ViewModels;
using Assistant.App.Views;
using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(Assistant.App.Tests.TestAppBuilder))]

namespace Assistant.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Assistant.App.App>()
            .WithInterFont()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

sealed class FakeSuggester : ISuggester
{
    public string Text = "";
    public ManualResetEventSlim? HoldAfterFirstChunk;
    public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        for (int i = 0; i < Text.Length; i += 9)
        {
            yield return Text.Substring(i, Math.Min(9, Text.Length - i));
            if (i >= 9 && HoldAfterFirstChunk is not null) await Task.Run(() => HoldAfterFirstChunk.Wait(4000, ct), ct);
        }
    }
}

sealed class FakeCapture : IScreenCapture
{
    public byte[] CapturePng(Region r) => new byte[] { 1 };
    public byte[] CaptureFullPng(Region r) => new byte[] { 1 };
    public (byte[] Bgra, int Width, int Height) GrabBgra(Region r) => (new byte[16], 2, 2);
}

sealed class NoWatcher : IScreenWatcher
{
    public bool Pending => false;
    public void Start() { }
    public void Dispose() { }
}

public class UiTests
{
    static readonly string ShotDir = Environment.GetEnvironmentVariable("SCREENSHOT_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    const string BothReply =
        "SAY\n• Yeah, Monday morning works for me.\n• Monday's good. Should I come to the main office first?\n" +
        "TYPE\n• Monday works, see you then! Do you want me to bring anything?\n";

    sealed class Rig : IDisposable
    {
        public Engine Engine = null!;
        public FakeSuggester Suggester = new();
        public MainViewModel Vm = null!;
        public MainWindow Window = null!;
        public readonly List<string> Copied = new();
        public Settings Settings = new();

        public static Rig Make(string reply = BothReply)
        {
            var r = new Rig();
            r.Suggester.Text = reply;
            r.Engine = new Engine(new EngineOptions { Debounce = TimeSpan.FromMilliseconds(30) }, r.Suggester, new FakeCapture(), (_, _, _) => new NoWatcher());
            r.Engine.Start();
            r.Vm = new MainViewModel(r.Engine, r.Settings, t => { r.Copied.Add(t); return Task.CompletedTask; },
                () => Task.CompletedTask, () => { }, () => { }, () => { });
            r.Window = new MainWindow { DataContext = r.Vm, Width = 440, Height = 780 };
            r.Window.Show();
            return r;
        }

        public void Dispose() { Window.Close(); Vm.Dispose(); Engine.Dispose(); }
    }

    static void Pump(Func<bool> until, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until())
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (DateTime.UtcNow > end) throw new TimeoutException("UI condition not reached");
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static void Settle()
    {
        for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(20); }
    }

    static void Shot(TopLevel window, string name)
    {
        Settle();
        Directory.CreateDirectory(ShotDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    static void Theme(ThemeVariant v) => Application.Current!.RequestedThemeVariant = v;

    // ---------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Main_window_empty_state()
    {
        using var rig = Rig.Make();
        rig.Vm.SetListening();
        Shot(rig.Window, "main-empty-light");
        Theme(ThemeVariant.Dark);
        Shot(rig.Window, "main-empty-dark");
        Theme(ThemeVariant.Light);
        Assert.False(rig.Vm.HasTurns);
        Assert.False(rig.Vm.HasSections);
    }

    [AvaloniaFact]
    public void Say_and_type_suggestions_render_and_copy()
    {
        using var rig = Rig.Make();
        rig.Vm.SetListening();
        rig.Engine.SetRegion(new Region(700, 120, 420, 160));
        rig.Engine.AddTurn(Speaker.Them, "Hey, can you start on Monday morning at nine?");
        rig.Engine.AddTurn(Speaker.Me, "I think so, let me check my calendar.");
        rig.Engine.AddTurn(Speaker.Them, "Great. Also, did you see my message about the schedule?");
        Pump(() => rig.Vm.Sections.Count == 2 && rig.Vm.Sections[1].Options.Count == 1 && rig.Vm.Sections[1].Options[0].IsComplete && rig.Vm.IsListening);

        Assert.True(rig.Vm.HasRegion);
        Assert.Contains("420", rig.Vm.RegionText);
        Assert.Equal(new[] { SectionKind.Say, SectionKind.Type }, rig.Vm.Sections.Select(s => s.Kind));
        Assert.Equal(2, rig.Vm.Sections[0].Options.Count);
        Assert.Equal(3, rig.Vm.Turns.Count);

        Shot(rig.Window, "main-suggestions-light");
        Theme(ThemeVariant.Dark);
        Shot(rig.Window, "main-suggestions-dark");
        Theme(ThemeVariant.Light);

        var option = rig.Vm.Sections[1].Options[0];
        option.CopyCommand.Execute(null);
        Pump(() => option.JustCopied);
        Assert.Equal("Monday works, see you then! Do you want me to bring anything?", Assert.Single(rig.Copied));
        Assert.Equal("Copied", option.CopyLabel);
    }

    [AvaloniaFact]
    public void Streaming_shows_partial_options_that_cannot_be_copied_yet()
    {
        using var rig = Rig.Make();
        rig.Suggester.HoldAfterFirstChunk = new ManualResetEventSlim();
        rig.Engine.AddTurn(Speaker.Them, "Can you walk me through your last project please?");
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsThinking);
        Assert.Contains(rig.Vm.Sections.SelectMany(s => s.Options), o => !o.IsComplete);
        Assert.Equal("Thinking…", rig.Vm.StatusText);
        Shot(rig.Window, "main-streaming-light");
        rig.Suggester.HoldAfterFirstChunk.Set();
        Pump(() => rig.Vm.IsListening);
        Assert.All(rig.Vm.Sections.SelectMany(s => s.Options), o => Assert.True(o.IsComplete));
    }

    [AvaloniaFact]
    public void Speech_model_download_progress_and_errors_show_in_the_status_area()
    {
        using var rig = Rig.Make();
        rig.Vm.SetPreparing("Downloading speech recognition… 42%", 0.42);
        Pump(() => rig.Vm.ShowProgress);
        Assert.Equal(42, rig.Vm.Progress, 3);
        Shot(rig.Window, "main-downloading-light");
        rig.Vm.SetProblem("Couldn't set up speech recognition: no internet connection", canRetry: true);
        Assert.True(rig.Vm.CanRetry);
        Assert.False(rig.Vm.ShowProgress);
        Shot(rig.Window, "main-error-light");
    }

    [AvaloniaFact]
    public void Clear_chat_empties_everything()
    {
        using var rig = Rig.Make();
        rig.Engine.AddTurn(Speaker.Them, "Can you walk me through your last project please?");
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsListening);
        rig.Vm.ClearChatCommand.Execute(null);
        Assert.False(rig.Vm.HasTurns);
        Assert.False(rig.Vm.HasSections);
        Assert.True(rig.Engine.Conversation.IsEmpty);
    }

    [AvaloniaFact]
    public void Settings_window_edits_apply_to_settings()
    {
        var settings = new Settings();
        int changes = 0;
        var entry = new KeyEntryViewModel(_ => Task.FromResult((true, "")));
        var vm = new SettingsViewModel(settings, () => changes++, entry, "sk-ant-api03-abcdef1234", _ => { });
        var win = new SettingsWindow { DataContext = vm, Width = 480, Height = 760 };
        win.Show();
        Shot(win, "settings-light");
        Theme(ThemeVariant.Dark);
        Shot(win, "settings-dark");
        Theme(ThemeVariant.Light);

        vm.ProfessionalismIndex = 3;
        vm.ProficiencyIndex = 0;
        vm.ToneIndex = 2;
        vm.LengthIndex = 0;
        vm.OptionsIndex = 2;
        vm.SelectedModel = Models.All[1];
        vm.UseMicrophone = true;
        vm.ReplyLanguage = "Spanish";
        Assert.Equal(Professionalism.Formal, settings.Professionalism);
        Assert.Equal(Proficiency.Simple, settings.Proficiency);
        Assert.Equal(Tone.Direct, settings.Tone);
        Assert.Equal(ReplyLength.Brief, settings.Length);
        Assert.Equal(3, settings.Options);
        Assert.Equal("claude-sonnet-5-5", settings.Model);
        Assert.True(settings.UseMicrophone);
        Assert.Equal("Spanish", settings.ReplyLanguage);
        Assert.True(changes >= 8);
        Assert.Equal("sk-ant-…1234", vm.MaskedKey);

        vm.ChangeKeyCommand.Execute(null);
        win.FindControl<ScrollViewer>("Scroller")!.ScrollToHome();
        Shot(win, "settings-key-edit-light");
        win.Close();
    }

    [AvaloniaFact]
    public async Task Welcome_screen_validates_the_key()
    {
        string? accepted = null;
        var entry = new KeyEntryViewModel(key => Task.FromResult(key.EndsWith("good") ? (true, "") : (false, "Claude didn't accept that key. Check it and try again.")));
        entry.Accepted = k => accepted = k;
        var win = new OnboardingWindow { DataContext = entry };
        win.Show();
        Shot(win, "welcome-light");
        Theme(ThemeVariant.Dark);
        Shot(win, "welcome-dark");
        Theme(ThemeVariant.Light);

        Assert.False(entry.SubmitCommand.CanExecute(null));
        entry.Key = "hello";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("sk-ant-", entry.Error);
        Assert.Null(accepted);

        entry.Key = "sk-ant-api03-bad";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("didn't accept", entry.Error);
        Assert.Null(accepted);
        Shot(win, "welcome-error-light");

        entry.Key = "  sk-ant-api03-good  ";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("sk-ant-api03-good", accepted);
        Assert.Equal("", entry.Error);

        Assert.Equal('•', entry.PasswordChar);
        entry.ToggleRevealCommand.Execute(null);
        Assert.Equal('\0', entry.PasswordChar);
        win.Close();
    }

    // ---------------------------------------------------------------------------------------

    static byte[] FakeScreenPng(int w, int h)
    {
        var host = new Border
        {
            Width = w, Height = h,
            Background = Avalonia.Media.Brushes.White,
            Child = new TextBlock { Text = "Alex: Can you start Monday morning?", FontSize = 22, Margin = new Thickness(60, 120, 0, 0) },
        };
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(w, h));
        bmp.Render(host);
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }

    sealed class PngCapture : IScreenCapture
    {
        public Region? LastFull;
        public byte[] CapturePng(Region r) => new byte[] { 1 };
        public byte[] CaptureFullPng(Region r) { LastFull = r; return FakeScreenPng(r.Width, r.Height); }
        public (byte[] Bgra, int Width, int Height) GrabBgra(Region r) => (new byte[16], 2, 2);
    }

    [AvaloniaFact]
    public async Task Area_picker_turns_a_drag_into_a_physical_pixel_region()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.Show();
        var capture = new PngCapture();
        IReadOnlyList<Window>? pickers = null;
        RegionPicker.WindowsShown = w => pickers = w;

        var pick = RegionPicker.PickAsync(owner, capture);
        while (pickers is null) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.False(owner.IsVisible); // our own window gets out of the way of the screenshot
        var picker = Assert.Single(pickers);
        Settle();
        picker.MouseDown(new Point(300, 200), Avalonia.Input.MouseButton.Left);
        picker.MouseMove(new Point(450, 280));
        picker.MouseMove(new Point(720, 360));
        Shot(picker, "picker-dragging");
        picker.MouseUp(new Point(720, 360), Avalonia.Input.MouseButton.Left);

        var region = await pick;
        RegionPicker.WindowsShown = null;
        var screen = owner.Screens.All[0];
        Assert.NotNull(region);
        Assert.Equal(new Region(screen.Bounds.X + 300, screen.Bounds.Y + 200, 420, 160), region);
        Assert.Equal(screen.Bounds.Width, capture.LastFull!.Value.Width);
        Assert.True(owner.IsVisible); // and comes back afterwards
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Area_picker_ignores_stray_clicks_and_cancels_on_escape()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.Show();
        IReadOnlyList<Window>? pickers = null;
        RegionPicker.WindowsShown = w => pickers = w;
        var pick = RegionPicker.PickAsync(owner, new PngCapture());
        while (pickers is null) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var picker = pickers[0];
        Settle();
        picker.MouseDown(new Point(100, 100), Avalonia.Input.MouseButton.Left);
        picker.MouseUp(new Point(104, 103), Avalonia.Input.MouseButton.Left); // too small: a click, not a selection
        Assert.False(pick.IsCompleted);
        picker.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Assert.Null(await pick);
        RegionPicker.WindowsShown = null;
        owner.Close();
    }
}
