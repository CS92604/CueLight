using System.Runtime.CompilerServices;
using Assistant.App;
using Assistant.App.ViewModels;
using Assistant.App.Views;
using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
    public TokenUsage? Report;                         // what the "API" says the request used
    public readonly List<SuggestionRequest> Calls = new();
    public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (Calls) Calls.Add(request);
        for (int i = 0; i < Text.Length; i += 9)
        {
            yield return Text.Substring(i, Math.Min(9, Text.Length - i));
            if (i >= 9 && HoldAfterFirstChunk is not null) await Task.Run(() => HoldAfterFirstChunk.Wait(4000, ct), ct);
        }
        if (Report is not null) request.OnUsage?.Invoke(Report);
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

/// <summary>Pretends the OS can hide windows from screen capture, for the duration of a test.</summary>
sealed class CaptureSupportStub : IDisposable
{
    private readonly Func<bool> _old = Assistant.App.Platform.CaptureShield.SupportCheck;
    public CaptureSupportStub(bool supported) => Assistant.App.Platform.CaptureShield.SupportCheck = () => supported;
    public void Dispose() => Assistant.App.Platform.CaptureShield.SupportCheck = _old;
}

public class UiTests
{
    static readonly string ShotDir = Environment.GetEnvironmentVariable("SCREENSHOT_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    const string BothReply =
        "SAY\n• Yeah, Monday morning works for me.\n• Monday's good. Should I come to the main office first?\n" +
        "TYPE\n• Monday works, see you then! Do you want me to bring anything?\n";

    internal sealed class Rig : IDisposable
    {
        public Engine Engine = null!;
        public FakeSuggester Suggester = new();
        public MainViewModel Vm = null!;
        public MainWindow Window = null!;
        public readonly List<string> Copied = new();
        public readonly List<bool> Recording = new();
        public Settings Settings = new();

        public Func<Task> PickRegion = () => Task.CompletedTask;

        public static Rig Make(string reply = BothReply, bool typeEnabled = true)
        {
            var r = new Rig();
            r.Suggester.Text = reply;
            r.Settings.TypeEnabled = typeEnabled;
            r.Engine = new Engine(new EngineOptions { Debounce = TimeSpan.FromMilliseconds(30) }, r.Suggester, new FakeCapture(), (_, _, _) => new NoWatcher());
            r.Engine.SetTextEnabled(typeEnabled);
            r.Engine.Start();
            r.Vm = new MainViewModel(r.Engine, r.Settings, t => { r.Copied.Add(t); return Task.CompletedTask; },
                () => r.PickRegion(), () => { }, () => { }, () => { }, on => r.Recording.Add(on));
            r.Vm.LiveWordInterval = TimeSpan.Zero;   // tests see each batch of live words at once; the reveal has tests of its own
            r.Window = new MainWindow { DataContext = r.Vm, Width = 440, Height = 780 };
            r.Window.Show();
            return r;
        }

        public void Dispose() { Window.Close(); Vm.Dispose(); Engine.Dispose(); }
    }

    internal static void Pump(Func<bool> until, int timeoutMs = 5000)
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

    internal static void Settle()
    {
        for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(20); }
    }

    internal static void Shot(TopLevel window, string name)
    {
        Settle();
        Directory.CreateDirectory(ShotDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    internal static void Theme(ThemeVariant v) => Application.Current!.RequestedThemeVariant = v;

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
        rig.Suggester.Report = new TokenUsage("claude-sonnet-5-5", 600, 150, 2_400, 220);   // about a third of a cent
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
    public void Panic_reads_the_text_area_now_even_with_auto_suggest_off()
    {
        using var rig = Rig.Make();
        rig.Engine.Options.AutoSuggest = false;
        rig.Engine.SetRegion(new Region(700, 120, 420, 160));
        rig.Vm.SetListening();
        rig.Vm.PanicCommand.Execute(null);
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsListening);
        var call = Assert.Single(rig.Suggester.Calls);
        Assert.True(call.Trigger.HasFlag(Trigger.Forced));
        Assert.NotNull(call.RegionPng);
    }

    [AvaloniaFact]
    public void Panic_without_a_text_area_asks_for_one_first()
    {
        using var rig = Rig.Make();
        int asked = 0;
        rig.PickRegion = () => { asked++; return Task.CompletedTask; };   // cancelled picker: no region
        rig.Vm.PanicCommand.Execute(null);
        Pump(() => asked == 1);
        Thread.Sleep(150);
        Assert.Empty(rig.Suggester.Calls);

        rig.PickRegion = () => { asked++; rig.Engine.SetRegion(new Region(10, 10, 300, 100)); return Task.CompletedTask; };
        rig.Vm.PanicCommand.Execute(null);
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsListening);
        Assert.Equal(2, asked);
        Assert.True(Assert.Single(rig.Suggester.Calls).Trigger.HasFlag(Trigger.Forced));
    }

    [AvaloniaFact]
    public void Regenerate_redoes_the_reply_and_takes_the_typed_direction()
    {
        using var rig = Rig.Make();
        rig.Vm.SetListening();
        Assert.False(rig.Vm.HasSections);                      // nothing to regenerate yet: button is hidden
        rig.Engine.AddTurn(Speaker.Them, "Can you walk me through your last project please?");
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsListening);
        Shot(rig.Window, "main-regenerate-light");

        rig.Vm.Hint = "shorter";
        rig.Vm.RegenerateCommand.Execute(null);
        Assert.Equal("", rig.Vm.Hint);
        Pump(() => rig.Suggester.Calls.Count == 2 && rig.Vm.IsListening);
        var redo = rig.Suggester.Calls[1];
        Assert.Equal("shorter", redo.Hint);
        Assert.NotNull(redo.Rejected);
        Assert.Contains("Monday morning works", redo.Rejected![0]);
        Assert.Equal(Trigger.Speech, redo.Trigger);
    }

    [AvaloniaFact]
    public void Recording_off_pauses_everything_and_says_so()
    {
        using var rig = Rig.Make();
        rig.Engine.SetRegion(new Region(700, 120, 420, 160));
        rig.Vm.SetListening();
        rig.Engine.AddTurn(Speaker.Them, "Hey, can you start on Monday morning at nine?");
        Pump(() => rig.Vm.Sections.Count == 2 && rig.Vm.IsListening);
        Assert.True(rig.Vm.IsRecording);
        Assert.Equal("Waiting for speech", rig.Vm.StatusLine);

        rig.Vm.IsRecording = false;
        Assert.True(rig.Engine.Paused);
        Assert.False(rig.Engine.IsWatching);
        Assert.Equal(new[] { false }, rig.Recording);
        Assert.Equal(MainViewModel.PausedText, rig.Vm.StatusLine);
        Assert.False(rig.Vm.IsListening);
        Shot(rig.Window, "main-paused-light");
        Theme(ThemeVariant.Dark);
        Shot(rig.Window, "main-paused-dark");
        Theme(ThemeVariant.Light);

        // The buttons do nothing while it's off.
        int calls = rig.Suggester.Calls.Count;
        rig.Vm.Hint = "shorter";
        rig.Vm.SuggestCommand.Execute(null);
        rig.Vm.PanicCommand.Execute(null);
        rig.Vm.RegenerateCommand.Execute(null);
        rig.Engine.AddTurn(Speaker.Them, "And are you able to bring your laptop along too?");
        Thread.Sleep(250);
        Assert.Equal(calls, rig.Suggester.Calls.Count);

        rig.Vm.IsRecording = true;
        Assert.False(rig.Engine.Paused);
        Assert.True(rig.Engine.IsWatching);
        Assert.Equal(new[] { false, true }, rig.Recording);
        Assert.Equal("Waiting for speech", rig.Vm.StatusLine);
        Assert.True(rig.Vm.IsListening);
    }

    [AvaloniaFact]
    public void Pausing_mid_reply_drops_the_cut_off_options()
    {
        using var rig = Rig.Make();
        rig.Suggester.HoldAfterFirstChunk = new ManualResetEventSlim();
        rig.Engine.AddTurn(Speaker.Them, "Can you walk me through your last project please?");
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsThinking);
        rig.Vm.IsRecording = false;
        Assert.False(rig.Vm.HasSections);
        Assert.Equal(StatusKind.Listening, rig.Vm.Status);       // not stuck on "Thinking…" when it comes back
        rig.Suggester.HoldAfterFirstChunk.Set();
    }

    [AvaloniaFact]
    public void Type_off_shows_only_say_and_hides_the_text_area_and_panic()
    {
        using var rig = Rig.Make(typeEnabled: false);
        rig.Vm.SetListening();
        Shot(rig.Window, "main-type-off-light");
        Assert.False(rig.Vm.TypeEnabled);
        Assert.False(rig.Engine.TextEnabled);
        Assert.Contains("as people talk.", rig.Vm.EmptyHint);

        rig.Engine.AddTurn(Speaker.Them, "Hey, can you start on Monday morning at nine?");
        Pump(() => rig.Vm.Sections.Count > 0 && rig.Vm.IsListening);
        Assert.Equal(new[] { SectionKind.Say }, rig.Vm.Sections.Select(x => x.Kind));   // the reply had a TYPE part; it's not shown
        Assert.Null(rig.Suggester.Calls[0].RegionPng);

        rig.Vm.PanicCommand.Execute(null);
        Thread.Sleep(200);
        Assert.Single(rig.Suggester.Calls);                       // Panic is a no-op with Type off

        // Turning Type on brings the TYPE part of the same reply back, and saves the choice.
        rig.Vm.TypeEnabled = true;
        Assert.True(rig.Settings.TypeEnabled);
        Assert.True(rig.Engine.TextEnabled);
        Assert.Equal(new[] { SectionKind.Say, SectionKind.Type }, rig.Vm.Sections.Select(x => x.Kind));
        Assert.Contains("text area changes", rig.Vm.EmptyHint);
    }

    [AvaloniaFact]
    public void Type_on_without_an_area_offers_to_select_one()
    {
        using var rig = Rig.Make(typeEnabled: false);
        rig.Vm.SetListening();
        rig.Vm.TypeEnabled = true;
        Assert.False(rig.Vm.HasRegion);
        Assert.Contains("Select the exact area", rig.Vm.RegionText);
        Shot(rig.Window, "main-type-on-light");
        Theme(ThemeVariant.Dark);
        Shot(rig.Window, "main-type-on-dark");
        Theme(ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void Capture_shield_flags_every_tracked_window_including_ones_opened_later()
    {
        var flagged = new List<(Window W, bool Hidden)>();
        var (oldApply, oldSupport) = (Platform.CaptureShield.Apply, Platform.CaptureShield.SupportCheck);
        Platform.CaptureShield.Apply = (w, hidden) => { flagged.Add((w, hidden)); return true; };
        Platform.CaptureShield.SupportCheck = () => true;
        try
        {
            var a = new Window();
            Platform.CaptureShield.Track(a);
            a.Show();
            Assert.Empty(flagged);                                 // off by default: nothing is touched

            Assert.True(Platform.CaptureShield.SetHidden(true));
            Assert.Contains((a, true), flagged);

            var b = new Window();                                  // e.g. the area picker, opened afterwards
            Platform.CaptureShield.Track(b);
            b.Show();
            Assert.Contains((b, true), flagged);

            flagged.Clear();
            Assert.True(Platform.CaptureShield.SetHidden(false));
            Assert.Contains((a, false), flagged);
            Assert.Contains((b, false), flagged);

            b.Close();
            flagged.Clear();
            Platform.CaptureShield.SetHidden(true);
            Assert.DoesNotContain(flagged, f => ReferenceEquals(f.W, b));  // closed windows are forgotten
            a.Close();
        }
        finally
        {
            Platform.CaptureShield.SetHidden(false);
            (Platform.CaptureShield.Apply, Platform.CaptureShield.SupportCheck) = (oldApply, oldSupport);
        }
    }

    [AvaloniaFact]
    public void Capture_shield_refuses_where_the_os_cannot_do_it()
    {
        var oldSupport = Platform.CaptureShield.SupportCheck;
        Platform.CaptureShield.SupportCheck = () => false;
        try
        {
            Assert.False(Platform.CaptureShield.SetHidden(true));
            Assert.False(Platform.CaptureShield.IsHidden);
            var vm = new SettingsViewModel(new Settings(), () => { }, new KeyEntryViewModel(), null, _ => { });
            Assert.False(vm.HideSupported);
            vm.HideFromCapture = true;
            Assert.False(vm.HideFromCapture);
        }
        finally { Platform.CaptureShield.SupportCheck = oldSupport; }
    }

    [AvaloniaFact]
    public void Hide_from_capture_setting_is_saved_when_supported()
    {
        var oldSupport = Platform.CaptureShield.SupportCheck;
        Platform.CaptureShield.SupportCheck = () => true;
        try
        {
            var settings = new Settings();
            int changes = 0;
            var vm = new SettingsViewModel(settings, () => changes++, new KeyEntryViewModel(), null, _ => { });
            vm.HideFromCapture = true;
            Assert.True(settings.HideFromCapture);
            Assert.True(vm.HideFromCapture);
            Assert.Equal(1, changes);
        }
        finally { Platform.CaptureShield.SupportCheck = oldSupport; }
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
        using var supported = new CaptureSupportStub(true);
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
        win.FindControl<ScrollViewer>("Scroller")!.ScrollToEnd();
        Shot(win, "settings-bottom-light");
        win.FindControl<ScrollViewer>("Scroller")!.ScrollToHome();

        vm.ProfessionalismIndex = 3;
        vm.ProficiencyIndex = 0;
        Assert.Equal(Settings.Caption(Proficiency.Simple), vm.ProficiencyCaption);
        vm.ToneIndex = 2;
        vm.LengthIndex = 0;
        vm.OptionsIndex = 2;
        Assert.Equal("claude-sonnet-5-5", vm.SelectedModel!.Id);   // the recommended, cheaper model is what a new install uses
        vm.SelectedModel = Models.All[0];
        vm.UseMicrophone = true;
        vm.ReplyLanguage = "Spanish";
        Assert.Equal(Professionalism.Formal, settings.Professionalism);
        Assert.Equal(Proficiency.Simple, settings.Proficiency);
        Assert.Equal(Tone.Direct, settings.Tone);
        Assert.Equal(ReplyLength.Brief, settings.Length);
        Assert.Equal(3, settings.Options);
        Assert.Equal("claude-opus-5-5", settings.Model);
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

    // -- hover explanations ----------------------------------------------------------------

    /// <summary>What the mouse would show: the nearest tip on the control or one of its parents.</summary>
    static string? TipFor(Control c)
    {
        for (Visual? v = c; v is not null; v = v.GetVisualParent())
            if (v is Control k && ToolTip.GetTip(k) is string t && t.Length > 0) return t;
        return null;
    }

    [AvaloniaFact]
    public void Every_button_and_switch_in_the_main_window_explains_itself_on_hover()
    {
        using var rig = Rig.Make();
        rig.Engine.AddTurn(Speaker.Them, "Can you start on Monday morning please?");
        Pump(() => rig.Vm.HasSections && rig.Vm.Status == StatusKind.Listening);
        rig.Vm.SetProblem("Couldn't set up speech recognition", canRetry: true); // makes Retry visible
        Settle();
        var controls = rig.Window.GetVisualDescendants().OfType<Control>()
            .Where(c => c is ToggleButton or ToggleSwitch or Button && c is not Avalonia.Controls.Primitives.ScrollBar and not RepeatButton)
            .Where(c => c.GetVisualAncestors().OfType<TextBox>().Any() == false)   // the text box's own inner parts
            .ToList();
        Assert.True(controls.Count >= 9, $"expected the buttons to be found, got {controls.Count}");
        foreach (var c in controls)
            Assert.False(string.IsNullOrWhiteSpace(TipFor(c)), $"{c.GetType().Name} {(c as ContentControl)?.Content} has no hover text");
    }

    [AvaloniaFact]
    public void Hover_text_explains_what_on_and_off_mean()
    {
        using var rig = Rig.Make();
        Settle();
        var toggles = rig.Window.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("pill")).ToList();
        Assert.Equal(2, toggles.Count);
        var recording = TipFor(toggles[0])!;
        Assert.Contains("On:", recording); Assert.Contains("Off:", recording);
        Assert.Contains("nothing is heard, watched or sent", recording);
        var type = TipFor(toggles[1])!;
        Assert.Contains("On:", type); Assert.Contains("Off:", type);
        var auto = rig.Window.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        Assert.Contains("Off: it only answers when you press", TipFor(auto)!);
    }

    [AvaloniaFact]
    public void Buttons_that_recording_turns_off_say_why_on_hover()
    {
        using var rig = Rig.Make();
        rig.Engine.AddTurn(Speaker.Them, "Can you start on Monday morning please?");
        Pump(() => rig.Vm.HasSections && rig.Vm.Status == StatusKind.Listening);
        Assert.DoesNotContain("Recording is off", rig.Vm.PanicTip + rig.Vm.SendTip + rig.Vm.RegenerateTip);
        rig.Vm.IsRecording = false;
        Settle();
        Assert.Contains("Recording is off", rig.Vm.PanicTip);
        Assert.Contains("Recording is off", rig.Vm.SendTip);
        Assert.Contains("Recording is off", rig.Vm.RegenerateTip);
        // disabled controls still show their tip (that's where the reason is)
        var panic = rig.Window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("panic"));
        Assert.False(panic.IsEffectivelyEnabled);
        Assert.Contains("Recording is off", ToolTip.GetTip(panic) as string);
        Assert.True(ToolTip.GetShowOnDisabled(panic), "disabled buttons must still show their hover text");
    }

    [AvaloniaFact]
    public void Every_option_in_settings_explains_itself_on_hover()
    {
        var vm = new SettingsViewModel(new Settings(), () => { }, new KeyEntryViewModel(), "sk-ant-api03-abcdef1234", _ => { });
        var win = new SettingsWindow { DataContext = vm, Width = 480, Height = 760 };
        win.Show();
        Settle();
        // Lists only create the rows that are on screen (and recycle the others), so check the rows at the top of the
        // page, then scroll and check the ones that appear.
        var tips = new List<(object Option, string Tip)>();
        void CheckVisibleRows()
        {
            foreach (var item in win.GetVisualDescendants().OfType<ListBoxItem>().ToList())
            {
                var tip = TipFor(item);
                Assert.False(string.IsNullOrWhiteSpace(tip), $"option '{item.DataContext}' has no hover text");
                if (item.DataContext is { } option && !tips.Any(t => ReferenceEquals(t.Option, option))) tips.Add((option, tip!));
            }
        }
        CheckVisibleRows();
        win.FindControl<ScrollViewer>("Scroller")!.ScrollToEnd();
        Settle();
        CheckVisibleRows();
        Assert.True(tips.Count >= 4 + 4 + 5 + 3 + 3 + 3 + 3, $"found {tips.Count} options"); // professionalism, proficiency, tone, length, options, models, speech
        foreach (var sw in win.GetVisualDescendants().OfType<ToggleSwitch>())
            Assert.False(string.IsNullOrWhiteSpace(TipFor(sw)), "a switch has no hover text");
        foreach (var box in win.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible))
            Assert.False(string.IsNullOrWhiteSpace(TipFor(box)), "a text field has no hover text");
        // each segment explains itself specifically, not just its group
        Assert.Contains("senior", tips.First(t => t.Option is ChoiceItem { Label: "Formal" }).Tip);
        Assert.Contains("fast PC", tips.First(t => t.Option is SpeechChoice { Value: SpeechAccuracy.Accurate }).Tip);
        win.Close();
    }

    [AvaloniaFact]
    public void The_hide_option_says_when_it_is_unavailable()
    {
        using (new CaptureSupportStub(false))
            Assert.Contains("isn't available", new SettingsViewModel(new Settings(), () => { }, new KeyEntryViewModel(), null, _ => { }).HideTip);
        using (new CaptureSupportStub(true))
            Assert.Contains("screen shares", new SettingsViewModel(new Settings(), () => { }, new KeyEntryViewModel(), null, _ => { }).HideTip);
    }

    [AvaloniaFact]
    public void The_running_cost_appears_in_the_corner_and_grows_with_use()
    {
        using var rig = Rig.Make();
        Assert.Equal("$0.00", rig.Vm.CostText);
        Assert.Contains("Nothing has been sent yet", rig.Vm.CostTip);

        rig.Suggester.Report = new TokenUsage("claude-sonnet-5-5", 1_000, 0, 5_000, 300);   // $0.0020 + $0.0005 + $0.0030
        rig.Engine.AddTurn(Speaker.Them, "Hey, can you start on Monday morning at nine?");
        Pump(() => rig.Vm.CostText != "$0.00");

        Assert.Equal("≈ $0.01", rig.Vm.CostText);   // $0.0055 rounds up
        Assert.Contains("1 request", rig.Vm.CostTip);
        Assert.Contains("83%", rig.Vm.CostTip);       // 5,000 of 6,000 input tokens were read from the cache
        Assert.Contains("Claude Console", rig.Vm.CostTip);
        var shown = Shown(rig.Window, "≈ $0.01");
        Assert.Equal(rig.Vm.CostTip, ToolTip.GetTip(shown));
    }

    static TextBlock Shown(Window w, string text) =>
        w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text && t.IsVisible);

    [Fact]
    public void The_cost_label_rounds_honestly()
    {
        UsageSnapshot Cost(decimal c, int requests = 3, bool unpriced = false) => new(requests, 0, 0, 0, 0, c, unpriced);
        Assert.Equal("$0.00", MainViewModel.CostLabel(Cost(0m, requests: 0)));
        Assert.Equal("≈ <$0.01", MainViewModel.CostLabel(Cost(0.004m)));
        Assert.Equal("≈ $0.01", MainViewModel.CostLabel(Cost(0.006m)));
        Assert.Equal("≈ $1.23", MainViewModel.CostLabel(Cost(1.234m)));
        Assert.Equal("≈ $12.50+", MainViewModel.CostLabel(Cost(12.5m, unpriced: true)));
        Assert.Contains("1 request.", MainViewModel.CostTipFor(Cost(0.1m, requests: 1)));
        Assert.Contains("3 requests.", MainViewModel.CostTipFor(Cost(0.1m, requests: 3)));
    }

    [AvaloniaFact]
    public void The_status_says_LISTENING_while_someone_is_speaking()
    {
        using var rig = Rig.Make();
        rig.Vm.SetListening();
        Assert.Equal("Waiting for speech", rig.Vm.StatusLine);
        Assert.False(rig.Vm.ShowHearing);

        rig.Engine.SetSpeaking(Speaker.Them, true);
        Pump(() => rig.Vm.StatusLine == "LISTENING");
        Settle();
        var label = rig.Window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "LISTENING");
        Assert.Contains("hearing", label.Classes);
        Assert.Equal(Avalonia.Media.FontWeight.Bold, label.FontWeight);
        var dot = rig.Window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().First(e => e.Classes.Contains("dot"));
        Assert.Contains("hearing", dot.Classes);
        Shot(rig.Window, "main-listening-light");

        rig.Engine.SetSpeaking(Speaker.Them, false);
        Pump(() => rig.Vm.StatusLine == "Waiting for speech");
        Assert.DoesNotContain("hearing", dot.Classes);
    }

    [AvaloniaFact]
    public void LISTENING_does_not_hide_a_problem_or_the_recording_switch()
    {
        using var rig = Rig.Make();
        rig.Vm.SetProblem("Lost the audio output device. Reconnecting as soon as it's back.");
        rig.Engine.SetSpeaking(Speaker.Them, true);
        Pump(() => rig.Vm.IsHearing);
        Assert.Equal("Lost the audio output device. Reconnecting as soon as it's back.", rig.Vm.StatusLine);   // problems still win

        rig.Vm.SetListening();
        Assert.Equal("LISTENING", rig.Vm.StatusLine);
        rig.Vm.IsRecording = false;                                  // recording off: nothing is heard
        Assert.Equal(MainViewModel.PausedText, rig.Vm.StatusLine);
        Pump(() => !rig.Vm.IsHearing);
        rig.Vm.IsRecording = true;
        Assert.Equal("Waiting for speech", rig.Vm.StatusLine);
    }

    [AvaloniaFact]
    public void LISTENING_shows_over_Thinking_and_Thinking_comes_back_after()
    {
        using var rig = Rig.Make();
        rig.Vm.SetListening();
        rig.Suggester.HoldAfterFirstChunk = new ManualResetEventSlim(false);
        rig.Engine.AddTurn(Speaker.Them, "Hey, can you start on Monday morning at nine?");
        Pump(() => rig.Vm.Status == StatusKind.Thinking);
        Assert.Equal("Thinking…", rig.Vm.StatusLine);

        rig.Engine.SetSpeaking(Speaker.Them, true);
        Pump(() => rig.Vm.StatusLine == "LISTENING");
        rig.Engine.SetSpeaking(Speaker.Them, false);
        Pump(() => rig.Vm.StatusLine == "Thinking…");
        rig.Suggester.HoldAfterFirstChunk.Set();
        Pump(() => rig.Vm.IsListening);
    }

    [AvaloniaFact]
    public void The_conversation_shows_words_as_they_are_said_with_animated_dots()
    {
        using var rig = Rig.Make();
        Assert.True(rig.Vm.ShowNothingYet);
        Assert.False(rig.Vm.ShowLive);

        rig.Engine.SetSpeaking(Speaker.Them, true);              // speech has begun, but no words yet: just the dots
        Pump(() => rig.Vm.ShowLive);
        Settle();
        Assert.False(rig.Vm.HasLiveText);
        Assert.False(rig.Vm.ShowNothingYet);
        var row = rig.Window.FindControl<Grid>("LiveRow")!;
        Assert.True(row.IsEffectivelyVisible);
        var dots = row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Where(e => e.Classes.Contains("typing")).ToList();
        Assert.Equal(3, dots.Count);
        Assert.All(dots, d => Assert.True(d.IsEffectivelyVisible));
        Assert.Equal("Them", rig.Vm.LiveWho);

        rig.Engine.SetLive(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as");
        Pump(() => rig.Vm.HasLiveText);
        Settle();
        var words = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == rig.Vm.LiveText);
        Assert.True(words.IsEffectivelyVisible);
        Assert.Equal("The dog that played Toto in the Wizard of Oz was credited as", words.Text);
        Shot(rig.Window, "main-live-light");

        rig.Engine.SetSpeaking(Speaker.Them, false);              // the sentence is over; the words stay until the transcript is in
        Pump(() => !rig.Vm.IsHearing);
        Assert.True(rig.Vm.ShowLive);
        Assert.True(rig.Vm.HasLiveText);

        rig.Engine.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as Toto.");
        rig.Engine.SetLive(Speaker.Them, null);
        Pump(() => !rig.Vm.ShowLive);
        Assert.Single(rig.Vm.Turns);
        Assert.False(row.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Live_words_are_revealed_a_few_at_a_time_and_never_wait_on_a_slow_batch()
    {
        using var rig = Rig.Make();
        rig.Vm.LiveWordInterval = TimeSpan.FromMinutes(10);        // the timer never fires on its own: the test takes each step
        rig.Engine.SetSpeaking(Speaker.Them, true);

        rig.Engine.SetLive(Speaker.Them, "one two three four five");
        Pump(() => rig.Vm.HasLiveText);
        Assert.Equal("one", rig.Vm.LiveText);                       // the first word is there at once
        Assert.Equal(4, rig.Vm.LiveWordsWaiting);
        rig.Vm.AdvanceLiveWords();
        Assert.Equal("one two", rig.Vm.LiveText);

        rig.Engine.SetLive(Speaker.Them, "one two three four five six seven");   // the next batch: what is showing stays
        Pump(() => rig.Vm.LiveWordsWaiting == 5);
        Assert.Equal("one two", rig.Vm.LiveText);

        rig.Engine.SetLive(Speaker.Them, "won two three four five six seven");   // an earlier word is corrected: it changes in place
        Pump(() => rig.Vm.LiveText == "won two");

        rig.Engine.SetLive(Speaker.Them, string.Join(' ', Enumerable.Range(1, 30).Select(i => $"w{i}")));   // a long queue is worked off faster
        Pump(() => rig.Vm.LiveWordsWaiting == 28);
        rig.Vm.AdvanceLiveWords();
        Assert.True(rig.Vm.LiveWordsWaiting <= 24, "a queue of 28 words shows more than one per step");
        int steps = 0;
        while (rig.Vm.LiveWordsWaiting > 0 && steps++ < 40) rig.Vm.AdvanceLiveWords();
        Assert.InRange(steps, 1, 20);                               // 24 words left take fewer than 24 steps
        Assert.EndsWith("w30", rig.Vm.LiveText);

        rig.Engine.SetLive(Speaker.Them, null);                      // the real transcript is in: the line goes at once
        Pump(() => !rig.Vm.HasLiveText);
        Assert.Equal(0, rig.Vm.LiveWordsWaiting);
        rig.Vm.AdvanceLiveWords();                                   // a stray step does nothing
        Assert.Equal("", rig.Vm.LiveText);
    }

    [AvaloniaFact]
    public void The_reveal_timer_shows_the_words_on_its_own()
    {
        using var rig = Rig.Make();
        rig.Vm.LiveWordInterval = TimeSpan.FromMilliseconds(20);
        rig.Engine.SetSpeaking(Speaker.Them, true);
        rig.Engine.SetLive(Speaker.Them, "can you start on Monday morning");
        Pump(() => rig.Vm.LiveText == "can you start on Monday morning");
        Assert.Equal(0, rig.Vm.LiveWordsWaiting);
    }

    [AvaloniaFact]
    public void Your_own_live_words_are_labelled_You_and_recording_off_hides_the_live_line()
    {
        using var rig = Rig.Make();
        rig.Engine.SetSpeaking(Speaker.Me, true);
        rig.Engine.SetLive(Speaker.Me, "I think so, let me check");
        Pump(() => rig.Vm.HasLiveText);
        Assert.Equal("You", rig.Vm.LiveWho);
        Assert.True(rig.Vm.LiveIsMe);

        rig.Vm.IsRecording = false;
        Pump(() => !rig.Vm.ShowLive);
        Assert.Equal("", rig.Vm.LiveText);
        Assert.True(rig.Vm.ShowNothingYet);
    }

    [AvaloniaFact]
    public void A_fuller_answer_gets_its_own_labelled_box_beside_the_conversational_reply()
    {
        using var rig = Rig.Make(reply:
            "SAY\n• It was Toto, believe it or not.\n• Toto. Same as the character.\n" +
            "ANSWER\n• The dog was credited as Toto, the character's own name. Her real name was Terry, and she earned more a week than many of the human cast.\n");
        Pump(() => rig.Vm.Sections.Count == 0);
        rig.Engine.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as what?");
        Pump(() => rig.Vm.Sections.Count == 2 && rig.Vm.Sections[1].Options.Count == 1 && rig.Vm.Sections[1].Options[0].IsComplete);
        Settle();

        Assert.Equal(new[] { SectionKind.Say, SectionKind.Answer }, rig.Vm.Sections.Select(s => s.Kind));
        Assert.Equal("Answer", rig.Vm.Sections[1].Title);
        Assert.Equal("in depth", rig.Vm.Sections[1].Hint);
        var chip = rig.Window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("chip") && b.Classes.Contains("answer") && b.IsEffectivelyVisible);
        Assert.Contains("ANSWER:", ToolTip.GetTip(chip) as string);
        Assert.Equal(3, rig.Window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("option") && b.IsEffectivelyVisible));   // two replies and the answer
        Shot(rig.Window, "main-answer-light");
    }

    [AvaloniaFact]
    public void Tooltip_card_screenshot()
    {
        // A ToolTip normally lives in a popup; drawn in place it shows how the card is styled and wraps.
        var tip = new ToolTip
        {
            IsVisible = true,
            Opacity = 1, // the real one fades in
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Content = "Recording. On: listens to your speakers (and your microphone, if you turned that on in Settings) and, when Type is on, watches the text area. Off: nothing is heard, watched or sent to Claude.",
        };
        var win = new Window { Width = 420, Height = 200, Content = new Border { Padding = new Thickness(40), Child = tip } };
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "tooltip-light"), (ThemeVariant.Dark, "tooltip-dark") })
        {
            Theme(variant);
            win.Show();
            Settle();
            Shot(win, name);
        }
        Theme(ThemeVariant.Default);
        win.Close();
    }
}
