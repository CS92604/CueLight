using System.Runtime.CompilerServices;
using Cuelight.Core;

namespace Cuelight.Tests;

sealed class FakeSuggester : ISuggester
{
    public readonly List<SuggestionRequest> Calls = new();
    public ManualResetEventSlim? Gate;                 // block after the first chunk until set
    public readonly ManualResetEventSlim FirstChunkSent = new();
    public Exception? Throw;
    public string[] Chunks = { "SAY\n• one", "\n• two" };
    public TokenUsage? Report;                         // what the "API" says the request used

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
        if (Report is not null) request.OnUsage?.Invoke(Report);
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

    static FakeSuggester Nothing() => new() { Chunks = new[] { "(nothing to respond to yet)" } };

    [Fact]
    public void A_long_pause_after_nothing_to_reply_to_asks_again_once_with_the_pause_noted()
    {
        var (eng, sug, rec) = Make(o => o.PauseFollowUp = TimeSpan.FromMilliseconds(150), sug: Nothing(), withRegion: true);
        eng.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as");
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);

        Assert.Equal(2, sug.Calls.Count);
        Assert.Equal(Trigger.Speech, sug.Calls[0].Trigger);
        Assert.Equal(Trigger.Pause, sug.Calls[1].Trigger);
        Assert.Null(sug.Calls[1].RegionPng);                       // a pause is about what was said, not the screen
        Assert.Contains("credited as", sug.Calls[1].Transcript);
        Thread.Sleep(500);
        Assert.Equal(2, sug.Calls.Count);                           // and only once: no loop of "still nothing"
    }

    [Fact]
    public void No_second_look_if_there_was_something_to_reply_to()
    {
        var (eng, sug, rec) = Make(o => o.PauseFollowUp = TimeSpan.FromMilliseconds(100));
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Thread.Sleep(400);
        Assert.Single(sug.Calls);
    }

    [Fact]
    public void No_second_look_if_more_was_said_in_the_meantime()
    {
        var (eng, sug, rec) = Make(o => o.PauseFollowUp = TimeSpan.FromMilliseconds(300), sug: Nothing());
        eng.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        eng.AddTurn(Speaker.Them, "Toto, but in reality the dog's name was Terry.");     // the quiet did not go on
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        Thread.Sleep(900);
        // The first follow-up was dropped because speech came in; only the last turn's own follow-up fired.
        Assert.Equal(1, sug.Calls.Count(c => c.Trigger == Trigger.Pause));
        Assert.Contains("Terry", sug.Calls.Last(c => c.Trigger == Trigger.Pause).Transcript);
    }

    [Fact]
    public void No_second_look_while_recording_is_off_or_someone_is_speaking_or_when_switched_off()
    {
        var (eng, sug, rec) = Make(o => o.PauseFollowUp = TimeSpan.FromMilliseconds(250), sug: Nothing());
        eng.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        eng.SetPaused(true);
        Thread.Sleep(600);
        Assert.DoesNotContain(sug.Calls, c => c.Trigger == Trigger.Pause);
        eng.SetPaused(false);

        var (eng2, sug2, rec2) = Make(o => o.PauseFollowUp = TimeSpan.FromMilliseconds(250), sug: Nothing());
        eng2.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as");
        rec2.WaitFor(EngineEventKind.SuggestEnd);
        eng2.SetSpeaking(Speaker.Them, true);                       // the next sentence has begun
        Thread.Sleep(600);
        Assert.DoesNotContain(sug2.Calls, c => c.Trigger == Trigger.Pause);

        var (eng3, sug3, rec3) = Make(o => o.PauseFollowUp = Timeout.InfiniteTimeSpan, sug: Nothing());
        eng3.AddTurn(Speaker.Them, "The dog that played Toto in the Wizard of Oz was credited as");
        rec3.WaitFor(EngineEventKind.SuggestEnd);
        Thread.Sleep(400);
        Assert.Single(sug3.Calls);
    }

    [Fact]
    public void What_each_request_used_is_added_up_and_announced()
    {
        var sug = new FakeSuggester { Report = new TokenUsage("claude-sonnet-5-5", 100, 500, 4_000, 300) };
        var (eng, _, rec) = Make(sug: sug);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.Usage);

        var u = eng.Usage.Snapshot();
        Assert.Equal(1, u.Requests);
        Assert.Equal((100, 500, 4_000, 300), (u.Input, u.CacheWrite, u.CacheRead, u.Output));
        // 100 x $2 + 500 x $2.50 + 4,000 x $0.10 + 300 x $10 per million
        Assert.Equal(0.00485m, u.Cost);
        Assert.False(u.Unpriced);
    }

    [Fact]
    public void Clearing_the_chat_does_not_reset_what_has_been_spent()
    {
        var sug = new FakeSuggester { Report = new TokenUsage("claude-sonnet-5-5", 1_000, 0, 0, 100) };
        var (eng, _, rec) = Make(sug: sug);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.Usage);
        eng.ClearConversation();
        Assert.Equal(1, eng.Usage.Snapshot().Requests);
    }

    [Fact]
    public void Requests_carry_only_the_recent_part_of_a_long_call()
    {
        var (eng, sug, rec) = Make(o => { o.TranscriptChars = 3_000; o.TranscriptKeepChars = 2_000; });
        for (int i = 0; i < 100; i++) eng.Conversation.Add(i % 2 == 0 ? Speaker.Me : Speaker.Them, $"Turn {i}: " + new string('x', 90));
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        var call = Assert.Single(sug.Calls);
        Assert.True(call.Transcript.Length <= 3_000, $"{call.Transcript.Length} characters");
        Assert.StartsWith("[earlier conversation omitted]", call.Transcript);
        Assert.EndsWith("Can you tell me about your last project?", call.Transcript);
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

    // -- Panic ------------------------------------------------------------------------------

    [Fact]
    public void Panic_reads_the_text_area_now_even_with_auto_suggest_off()
    {
        var (eng, sug, rec) = Make(o => { o.AutoSuggest = false; o.Debounce = TimeSpan.FromSeconds(30); }, withRegion: true);
        Assert.True(eng.Panic());
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 2000);
        var call = Assert.Single(sug.Calls);
        Assert.True(call.Trigger.HasFlag(Trigger.Forced));
        Assert.NotNull(call.RegionPng);
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.SuggestStart && e.Text == "Reading the text area…");
    }

    [Fact]
    public void Panic_does_not_wait_for_a_changing_screen_to_settle()
    {
        var (eng, sug, rec) = Make(o => o.MergeHold = TimeSpan.FromSeconds(5), withRegion: true);
        FakeWatcher.Instances[^1].Pending = true;    // the chat is still mid-change
        eng.Panic();
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 1500);
        Assert.Single(sug.Calls);
    }

    [Fact]
    public void Panic_without_a_text_area_says_so_and_sends_nothing()
    {
        var (eng, sug, rec) = Make();
        Assert.False(eng.Panic());
        Thread.Sleep(150);
        Assert.Empty(sug.Calls);
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.Status && e.Text!.Contains("text area"));
    }

    [Fact]
    public void Panic_takes_over_a_reply_in_progress_and_keeps_what_it_owed()
    {
        var gate = new ManualResetEventSlim();
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, new FakeSuggester { Gate = gate }, withRegion: true);
        eng.AddTurn(Speaker.Them, "Hey, are you still there with us on the call?");
        Assert.True(sug.FirstChunkSent.Wait(3000));
        eng.Panic();
        gate.Set();
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 5000);
        Thread.Sleep(200);
        Assert.Equal(2, sug.Calls.Count);
        Assert.Equal(Trigger.Speech | Trigger.Manual | Trigger.Forced, sug.Calls[1].Trigger);
        Assert.NotNull(sug.Calls[1].RegionPng);
    }

    // -- Regenerate -------------------------------------------------------------------------

    [Fact]
    public void Regenerate_redoes_the_last_request_and_passes_the_thrown_away_reply()
    {
        var (eng, sug, rec) = Make(withRegion: true);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        Assert.Equal(2, sug.Calls.Count);
        Assert.Null(sug.Calls[0].Rejected);
        Assert.Equal(Trigger.Speech, sug.Calls[1].Trigger);
        Assert.Null(sug.Calls[1].RegionPng);                      // a spoken-only request stays spoken-only
        Assert.Equal(new[] { "SAY\n• one\n• two" }, sug.Calls[1].Rejected);
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.SuggestStart && e.Text == "Trying another take…");
    }

    [Fact]
    public void Regenerating_again_remembers_every_reply_up_to_a_cap()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false, withRegion: true);
        eng.Request();
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        for (int i = 2; i <= 6; i++)
        {
            eng.Regenerate();
            rec.WaitFor(EngineEventKind.SuggestEnd, i);
        }
        Assert.Equal(new[] { 0, 1, 2, 3, 3, 3 }, sug.Calls.Select(c => c.Rejected?.Count ?? 0));
    }

    [Fact]
    public void Regenerate_keeps_the_hint_until_given_a_new_one()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false);
        eng.Request(hint: "shorter");
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        eng.Regenerate("friendlier");
        rec.WaitFor(EngineEventKind.SuggestEnd, 3);
        Assert.Equal(new[] { "shorter", "shorter", "friendlier" }, sug.Calls.Select(c => c.Hint));
    }

    [Fact]
    public void Regenerate_uses_the_current_transcript_and_settings()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        eng.Request();
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        eng.AddTurn(Speaker.Me, "Sure, it was a booking system.");
        eng.Settings = new Settings { Professionalism = Professionalism.Formal };
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        Assert.Contains("booking system", sug.Calls[1].Transcript);
        Assert.Equal(Professionalism.Formal, sug.Calls[1].Settings.Professionalism);
    }

    [Fact]
    public void A_new_moment_forgets_the_thrown_away_replies()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        eng.AddTurn(Speaker.Them, "And what did you enjoy most about it?");
        rec.WaitFor(EngineEventKind.SuggestEnd, 3);
        Assert.NotNull(sug.Calls[1].Rejected);
        Assert.Null(sug.Calls[2].Rejected);
    }

    [Fact]
    public void Regenerating_midway_restarts_once_and_throws_away_the_partial_reply()
    {
        var gate = new ManualResetEventSlim();
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, new FakeSuggester { Gate = gate });
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        Assert.True(sug.FirstChunkSent.Wait(3000));
        eng.Regenerate();
        gate.Set();
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 5000);
        Thread.Sleep(300);
        Assert.Equal(2, sug.Calls.Count);                         // no duplicate from the cancelled run's debt
        Assert.Equal(new[] { "SAY\n• one" }, sug.Calls[1].Rejected);
    }

    [Fact]
    public void Regenerate_with_nothing_to_redo_is_a_plain_manual_request()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false);
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Equal(Trigger.Manual, sug.Calls[0].Trigger);
        Assert.Null(sug.Calls[0].Rejected);
    }

    [Fact]
    public void A_nothing_to_respond_reply_is_not_held_against_the_next_try()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false, new FakeSuggester { Chunks = new[] { "(nothing to respond to yet)" } });
        eng.Request();
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        Assert.Null(sug.Calls[1].Rejected);
    }

    [Fact]
    public void Clearing_the_conversation_forgets_what_there_was_to_regenerate()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false, withRegion: true);
        eng.Request(Trigger.Speech);
        rec.WaitFor(EngineEventKind.SuggestEnd, 1);
        eng.ClearConversation();
        eng.Regenerate();
        rec.WaitFor(EngineEventKind.SuggestEnd, 2);
        Assert.Equal(Trigger.Manual, sug.Calls[1].Trigger);
        Assert.Null(sug.Calls[1].Rejected);
    }

    // -- Recording off / Type off -----------------------------------------------------------

    [Fact]
    public void While_paused_nothing_triggers_and_nothing_is_sent()
    {
        var (eng, sug, rec) = Make(withRegion: true);
        eng.SetPaused(true);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");   // a late transcript still lands in the chat...
        eng.OnScreenChanged();
        eng.Request();
        eng.Request(Trigger.Speech, hint: "x");
        Assert.False(eng.Panic());
        eng.Regenerate();
        Thread.Sleep(300);
        Assert.Empty(sug.Calls);                                                    // ...but never reaches Claude
        Assert.Contains("Can you tell me", eng.Conversation.Render(1000));
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.Status && e.Text!.Contains("Recording is off"));
    }

    [Fact]
    public void Pausing_stops_the_watcher_and_resuming_starts_a_fresh_one_on_the_same_region()
    {
        var (eng, _, rec) = Make(withRegion: true);
        var first = FakeWatcher.Instances[^1];
        Assert.True(eng.IsWatching);
        eng.SetPaused(true);
        Assert.True(first.Disposed);
        Assert.False(eng.IsWatching);
        Assert.Equal(Area, eng.Region);                      // the selection survives
        eng.SetPaused(false);
        var second = FakeWatcher.Instances[^1];
        Assert.NotSame(first, second);
        Assert.True(second.Started);
        Assert.True(eng.IsWatching);
        Assert.Equal(2, rec.Events.Count(e => e.Kind == EngineEventKind.ModeChanged));
    }

    [Fact]
    public void Pausing_cancels_a_reply_in_flight_and_resuming_does_not_resurrect_it()
    {
        var gate = new ManualResetEventSlim();
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, new FakeSuggester { Gate = gate }, withRegion: true);
        eng.AddTurn(Speaker.Them, "Hey, are you still there with us on the call?");
        Assert.True(sug.FirstChunkSent.Wait(3000));
        eng.SetPaused(true);
        gate.Set();
        Thread.Sleep(300);
        eng.SetPaused(false);
        Thread.Sleep(400);
        Assert.Single(sug.Calls);                            // the interrupted request isn't retried on resume
        Assert.Equal(0, rec.Count(EngineEventKind.SuggestEnd));
    }

    [Fact]
    public void Pausing_drops_what_was_waiting_to_be_sent()
    {
        var (eng, sug, _) = Make(o => o.Debounce = TimeSpan.FromMilliseconds(300), withRegion: true);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        eng.SetPaused(true);
        Thread.Sleep(600);
        eng.SetPaused(false);
        Thread.Sleep(400);
        Assert.Empty(sug.Calls);
    }

    [Fact]
    public void Requests_work_again_after_resuming()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, withRegion: true);
        eng.SetPaused(true);
        eng.SetPaused(false);
        eng.AddTurn(Speaker.Them, "Can you tell me about your last project?");
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Single(sug.Calls);
    }

    [Fact]
    public void With_type_off_the_region_is_not_watched_or_sent()
    {
        var (eng, sug, rec) = Make(o => { o.AutoSuggest = false; }, withRegion: true);
        var watcher = FakeWatcher.Instances[^1];
        eng.SetTextEnabled(false);
        Assert.True(watcher.Disposed);
        Assert.False(eng.IsWatching);
        Assert.Equal(Area, eng.Region);                      // kept for when Type comes back on
        eng.Request();
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.Null(sug.Calls[0].RegionPng);
        Assert.Equal(Trigger.Manual, sug.Calls[0].Trigger);
    }

    [Fact]
    public void With_type_off_a_region_picked_later_is_remembered_but_not_watched()
    {
        var (eng, _, _) = Make();
        eng.SetTextEnabled(false);
        eng.SetRegion(Area);
        Assert.Empty(FakeWatcher.Instances);
        eng.SetTextEnabled(true);
        Assert.True(Assert.Single(FakeWatcher.Instances).Started);
    }

    [Fact]
    public void With_type_off_screen_changes_and_panic_do_nothing()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.Zero, withRegion: true);
        eng.SetTextEnabled(false);
        eng.OnScreenChanged();
        Assert.False(eng.Panic());
        Thread.Sleep(250);
        Assert.Empty(sug.Calls);
        Assert.Contains(rec.Events, e => e.Kind == EngineEventKind.Status && e.Text!.Contains("Type on"));
    }

    [Fact]
    public void Turning_type_back_on_makes_panic_work_again()
    {
        var (eng, sug, rec) = Make(o => o.AutoSuggest = false, withRegion: true);
        eng.SetTextEnabled(false);
        eng.SetTextEnabled(true);
        Assert.True(eng.Panic());
        rec.WaitFor(EngineEventKind.SuggestEnd);
        Assert.NotNull(sug.Calls[0].RegionPng);
    }

    [Fact]
    public void Turning_type_off_drops_a_pending_text_trigger_but_keeps_speech()
    {
        var (eng, sug, rec) = Make(o => o.Debounce = TimeSpan.FromMilliseconds(300), withRegion: true);
        eng.AddTurn(Speaker.Them, "Did you get my message about the schedule?");
        eng.OnScreenChanged();                               // both are waiting out the debounce
        eng.SetTextEnabled(false);
        rec.WaitFor(EngineEventKind.SuggestEnd, timeoutMs: 3000);
        Thread.Sleep(300);
        var call = Assert.Single(sug.Calls);
        Assert.Equal(Trigger.Speech, call.Trigger);
        Assert.Null(call.RegionPng);
    }

    [Fact]
    public void Turning_type_off_cancels_a_lone_pending_text_trigger()
    {
        var (eng, sug, _) = Make(o => o.Debounce = TimeSpan.FromMilliseconds(300), withRegion: true);
        eng.OnScreenChanged();
        eng.SetTextEnabled(false);
        Thread.Sleep(700);
        Assert.Empty(sug.Calls);
    }
}
