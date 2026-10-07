using System.Runtime.CompilerServices;
using Assistant.Core;

namespace Assistant.Tests;

sealed class FakeSuggester : ISuggester
{
    public readonly List<SuggestionRequest> Calls = new();
    public ManualResetEventSlim? Gate;                 // block after the first chunk until set
    public readonly ManualResetEventSlim FirstChunkSent = new();
    public Exception? Throw;
    public string[] Chunks = { "SAY\n• one", "\n• two" };

    public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (Calls) Calls.Add(request);
        if (Throw is not null) throw Throw;
        for (int i = 0; i < Chunks.Length; i++)
        {
            yield return Chunks[i];
            if (i == 0 && Gate is not null)
            {
                FirstChunkSent.Set();
                await Task.Run(() => Gate.Wait(TimeSpan.FromSeconds(3), ct), ct);
            }
        }
    }
}

sealed class FakeCapture : IScreenCapture
{
    public bool Fail;
    public byte[] CapturePng(Region r) => Fail ? throw new IOException("no display") : new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' };
    public byte[] CaptureFullPng(Region r) => new byte[] { 1 };
    public (byte[] Bgra, int Width, int Height) GrabBgra(Region r) => (new byte[16], 2, 2);
}

sealed class FakeWatcher : IScreenWatcher
{
    public static readonly List<FakeWatcher> Instances = new();
    public bool Pending { get; set; }
    public bool Started, Disposed;
    public Action OnChange = () => { };
    public void Start() => Started = true;
    public void Dispose() => Disposed = true;
}

sealed class Recorder
{
    public readonly List<EngineEvent> Events = new();
    public void Add(EngineEvent e) { lock (Events) Events.Add(e); }
    public int Count(EngineEventKind k) { lock (Events) return Events.Count(e => e.Kind == k); }

    public void WaitFor(EngineEventKind kind, int count = 1, int timeoutMs = 4000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Count(kind) < count)
        {
            if (DateTime.UtcNow > end) throw new TimeoutException($"timed out waiting for {kind}: {string.Join(", ", Events.Select(e => e.Kind))}");
            Thread.Sleep(10);
        }
    }
}

public class EngineTests : IDisposable
{
    static readonly Region Area = new(10, 20, 300, 200);
    readonly List<Engine> _engines = new();

    (Engine Engine, FakeSuggester Sug, Recorder Rec) Make(Action<EngineOptions>? tweak = null, FakeSuggester? sug = null,
        FakeCapture? capture = null, bool withRegion = false)
    {
        FakeWatcher.Instances.Clear();
        var options = new EngineOptions { Debounce = TimeSpan.FromMilliseconds(50) };
        tweak?.Invoke(options);
        sug ??= new FakeSuggester();
        var engine = new Engine(options, sug, capture ?? new FakeCapture(), (r, onChange, onError) =>
        {
            var w = new FakeWatcher { OnChange = onChange };
            FakeWatcher.Instances.Add(w);
            return w;
        });
        var rec = new Recorder();
        engine.Event += rec.Add;
        engine.Start();
        _engines.Add(engine);
        if (withRegion) engine.SetRegion(Area);
        return (engine, sug, rec);
    }

    public void Dispose() { foreach (var e in _engines) e.Dispose(); }

    [Fact]
    public void Speech_triggers_a_say_request_without_image()
    {
        var (eng, sug, rec) = Make(withRegion: true);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        var call = Assert.Single(sug.Calls);
        Assert.Equal(Trigger.Speech, call.Trigger);
        Assert.Null(call.RegionPng);
        Assert.Contains("Them: Can you tell me about your last project?", call.Transcript);
        Assert.Equal("SAY\n• one\n• two", string.Concat(rec.Events.Where(e => e.Kind == EngineEventKind.Chunk).Select(e => e.Text)));
    }

    [Fact]
    public void My_own_speech_and_short_backchannels_do_not_trigger()
    {
        var (eng, sug, rec) = Make();
        eng.AddTurn(Speaker.Me, "I built a scheduling service at my last job.");
        eng.AddTurn(Speaker.Them, "mm-hmm");
        Thread.Sleep(300);
        Assert.Empty(sug.Calls);
        Assert.Equal(2, rec.Count(EngineEventKind.Turn));
    }

    [Fact]
    public void Rapid_utterances_are_coalesced_into_one_request()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.FromMilliseconds(200));
        eng.AddTurn(Speaker.Them, "So tell me about yourself and your background.");
        eng.AddTurn(Speaker.Them, "And why you want this particular role here.");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Thread.Sleep(400);
        var call = Assert.Single(sug.Calls);
        Assert.Contains("background. And why you want", call.Transcript);
    }

    [Fact]
    public void Screen_change_triggers_a_type_request_with_the_region_image()
    {
        var (eng, sug, rec) = Make(withRegion: true);
        Assert.True(FakeWatcher.Instances[^1].Started);
        eng.OnScreenChanged();
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Equal(Trigger.Text, sug.Calls[0].Trigger);
        Assert.NotNull(sug.Calls[0].RegionPng);
    }

    [Fact]
    public void Speech_and_screen_change_together_ask_for_both()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.FromMilliseconds(200), withRegion: true);
        eng.AddTurn(Speaker.Them, "Did you get my message about the schedule?");
        eng.OnScreenChanged();
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Thread.Sleep(400);
        var call = Assert.Single(sug.Calls);
        Assert.Equal(Trigger.Speech | Trigger.Text, call.Trigger);
        Assert.NotNull(call.RegionPng);
    }

    [Fact]
    public void An_interrupted_text_request_is_not_forgotten_when_speech_arrives()
    {
        var gate = new ManualResetEventSlim();
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, new FakeSuggester { Gate = gate }, withRegion: true);
        eng.OnScreenChanged();                       // run 1: TEXT, blocks after its first chunk
        Assert.True(sug.FirstChunkSent.Wait(3000));
        eng.AddTurn(Speaker.Them, "Hey, are you still there with us on the call?"); // supersedes run 1
        gate.Set();
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 5000);
        Assert.Equal(Trigger.Text, sug.Calls[0].Trigger);
        Assert.Equal(Trigger.Speech | Trigger.Text, sug.Calls[1].Trigger);
    }

    [Fact]
    public void Clearing_the_conversation_drops_what_an_interrupted_run_owed()
    {
        var gate = new ManualResetEventSlim();
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, new FakeSuggester { Gate = gate }, withRegion: true);
        eng.OnScreenChanged();
        Assert.True(sug.FirstChunkSent.Wait(3000));
        eng.ClearConversation();
        gate.Set();
        Thread.Sleep(300);
        eng.Request(Trigger.Speech);                 // a fresh request must not inherit the old TEXT trigger
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 5000);
        Assert.Equal(Trigger.Speech, sug.Calls[^1].Trigger);
    }

    [Fact]
    public void Auto_off_ignores_triggers_but_manual_works()
    {
        var (eng, sug, rec) = Make(o => { o.AutoSuggest = false; o.Debounce = TimeSpan.Zero; }, withRegion: true);
        eng.AddTurn(Speaker.Them, "What are your salary expectations for this role?");
        eng.OnScreenChanged();
        Thread.Sleep(200);
        Assert.Empty(sug.Calls);
        eng.Request(hint: "keep it polite and short");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Equal("keep it polite and short", sug.Calls[0].Hint);
        Assert.Equal(Trigger.Manual, sug.Calls[0].Trigger);
        Assert.NotNull(sug.Calls[0].RegionPng); // manual requests include what's on screen
    }

    [Fact]
    public void Clearing_the_region_stops_watching()
    {
        var (eng, _, rec) = Make(withRegion: true);
        var watcher = FakeWatcher.Instances[^1];
        eng.SetRegion(null);
        eng.OnScreenChanged();                       // a late callback from the old watcher must do nothing
        Thread.Sleep(150);
        Assert.True(watcher.Disposed);
        Assert.Null(eng.Region);
        Assert.Equal(0, rec.Count(EngineEventKind.SuggestStart));
    }

    [Fact]
    public void Screenshot_failure_degrades_gracefully()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, capture: new FakeCapture { Fail = true }, withRegion: true);
        eng.OnScreenChanged();
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Null(sug.Calls[0].RegionPng);
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.Status && e.Text!.Contains("no display"));
    }

    [Fact]
    public void Spoken_request_waits_for_a_settling_screen_change_and_merges()
    {
        var (eng, sug, rec) = Make(o => { o.Debounce = TimeSpan.FromMilliseconds(50); o.MergeHold = TimeSpan.FromSeconds(3); }, withRegion: true);
        var watcher = FakeWatcher.Instances[^1];
        watcher.Pending = true;                      // the chat text is mid-change
        eng.AddTurn(Speaker.Them, "Hey, did you see the message I just sent you?");
        Thread.Sleep(400);
        Assert.Empty(sug.Calls);                     // held, not sent as speech-only
        watcher.Pending = false;
        eng.OnScreenChanged();                       // the text settled
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Thread.Sleep(200);
        Assert.Equal(Trigger.Speech | Trigger.Text, Assert.Single(sug.Calls).Trigger);
    }

    [Fact]
    public void The_hold_is_bounded_if_the_screen_never_settles()
    {
        var (eng, sug, rec) = Make(o => { o.Debounce = TimeSpan.FromMilliseconds(50); o.MergeHold = TimeSpan.FromMilliseconds(500); }, withRegion: true);
        FakeWatcher.Instances[^1].Pending = true;    // e.g. a video playing in the watched area
        eng.AddTurn(Speaker.Them, "Hey, did you see the message I just sent you?");
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 3000);
        Assert.Equal(Trigger.Speech, sug.Calls[0].Trigger);
    }

    [Fact]
    public void Errors_are_reported_not_thrown()
    {
        var (eng, _, rec) = Make(o => o.AutoSuggest = false, new FakeSuggester { Throw = new InvalidOperationException("kaput") });
        eng.Request();
        rec.WaitFor(EngineEventKind.Error);
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.Error && e.Text!.Contains("kaput"));
    }

    [Fact]
    public void Settings_are_applied_to_the_next_request()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false);
        eng.Settings = new Settings { Professionalism = Professionalism.Formal };
        eng.Request();
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Equal(Professionalism.Formal, sug.Calls[0].Settings.Professionalism);
    }
}
