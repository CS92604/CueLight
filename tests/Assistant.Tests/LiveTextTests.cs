using Assistant.Core;

namespace Assistant.Tests;

public class LivePreviewAudioTests
{
    const int Rate = 16000;
    static float[] Tone(double seconds, float amp = 0.2f) =>
        Enumerable.Range(0, (int)(Rate * seconds)).Select(i => amp * (float)Math.Sin(2 * Math.PI * 220 * i / Rate)).ToArray();
    static float[] Silence(double seconds) => new float[(int)(Rate * seconds)];

    [Fact]
    public void The_speech_so_far_is_available_only_while_someone_is_speaking_and_can_be_capped()
    {
        var seg = new Segmenter();
        Assert.Null(seg.SnapshotSpeech());
        seg.Feed(Silence(0.5));
        Assert.Null(seg.SnapshotSpeech());

        seg.Feed(Tone(2.0));
        var two = seg.SnapshotSpeech()!;
        Assert.InRange(two.Length / (double)Rate, 2.0, 2.8);       // the speech plus a little run-up

        seg.Feed(Tone(4.0));
        var six = seg.SnapshotSpeech()!;
        Assert.True(six.Length > two.Length);
        Assert.InRange(seg.SnapshotSpeech(maxSeconds: 3)!.Length / (double)Rate, 2.9, 3.1);

        seg.Feed(Silence(1.0));                                      // the pause ends it
        Assert.Null(seg.SnapshotSpeech());
    }

    [Fact]
    public void Previews_come_about_once_a_second_while_speech_goes_on_and_not_in_silence()
    {
        var previews = new List<float[]>();
        var ingest = new AudioIngest(Rate, 1, _ => { });
        ingest.Partial += previews.Add;

        void Feed(float[] audio)
        {
            for (int i = 0; i < audio.Length; i += 1600) ingest.Feed(audio.AsSpan(i, Math.Min(1600, audio.Length - i)));
        }

        Feed(Silence(1.0));
        Assert.Empty(previews);

        Feed(Tone(3.0));
        Assert.InRange(previews.Count, 2, 4);                       // first after about half a second, then every second
        Assert.True(previews.Zip(previews.Skip(1), (a, b) => b.Length > a.Length).All(x => x), "each preview holds more speech than the last");

        int before = previews.Count;
        Feed(Silence(2.0));
        Assert.Equal(before, previews.Count);
    }

    [Fact]
    public void A_click_never_produces_a_preview()
    {
        var previews = new List<float[]>();
        var ingest = new AudioIngest(Rate, 1, _ => { });
        ingest.Partial += previews.Add;
        ingest.Feed(Silence(1.0));
        ingest.Feed(Tone(0.1));
        ingest.Feed(Silence(1.5));
        Assert.Empty(previews);
    }
}

public class LiveTextEngineTests
{
    static Engine NewEngine(List<EngineEvent> events)
    {
        var e = new Engine(new EngineOptions { AutoSuggest = false }, new FakeSuggester(), new FakeCapture());
        e.Event += ev => { lock (events) events.Add(ev); };
        return e;
    }

    [Fact]
    public void Live_words_are_kept_per_speaker_announced_on_change_and_cleared_by_null()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        Assert.Null(engine.LiveText(Speaker.Them));

        engine.SetLive(Speaker.Them, "Can you start");
        engine.SetLive(Speaker.Them, "Can you start");                // no change: no event
        engine.SetLive(Speaker.Them, "Can you start on Monday");
        Assert.Equal("Can you start on Monday", engine.LiveText(Speaker.Them));
        Assert.Equal(2, events.Count(e => e.Kind == EngineEventKind.Live));
        Assert.Equal("Can you start on Monday", events.Last(e => e.Kind == EngineEventKind.Live).Text);

        engine.SetLive(Speaker.Me, "Sure");
        Assert.Equal("Sure", engine.LiveText(Speaker.Me));
        Assert.Equal("Can you start on Monday", engine.LiveText(Speaker.Them));

        engine.SetLive(Speaker.Them, "   ");                          // nothing to show
        Assert.Null(engine.LiveText(Speaker.Them));
        Assert.Null(events.Last(e => e.Kind == EngineEventKind.Live && e.Speaker == Speaker.Them).Text);
    }

    [Fact]
    public void Live_words_are_not_part_of_the_conversation_sent_to_claude()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        engine.SetLive(Speaker.Them, "half a sentence that");
        Assert.True(engine.Conversation.IsEmpty);
        engine.AddTurn(Speaker.Them, "half a sentence that is now complete");
        Assert.DoesNotContain("half a sentence that\n", engine.Conversation.Render(1000) + "\n");
    }

    [Fact]
    public void Turning_recording_off_clears_the_live_words_and_ignores_later_ones()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        engine.SetLive(Speaker.Them, "Can you start");
        engine.SetPaused(true);
        Assert.Null(engine.LiveText(Speaker.Them));
        engine.SetLive(Speaker.Them, "late news from the device");
        Assert.Null(engine.LiveText(Speaker.Them));
    }

    [Fact]
    public void Who_is_speaking_prefers_the_other_person()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        Assert.Null(engine.HearingWho);
        engine.SetSpeaking(Speaker.Me, true);
        Assert.Equal(Speaker.Me, engine.HearingWho);
        engine.SetSpeaking(Speaker.Them, true);
        Assert.Equal(Speaker.Them, engine.HearingWho);
    }
}

public class LivePreviewPipelineTests
{
    sealed class Mic : IAudioSource
    {
        public event Action<float[]>? Utterance;
        public event Action<string>? Failed { add { } remove { } }
        public event Action<bool>? Speaking;
        public event Action<float[]>? Partial;
        public void Start() { }
        public void Dispose() { }
        public void Speak(bool on) => Speaking?.Invoke(on);
        public void Preview(float[] a) => Partial?.Invoke(a);
        public void Finish(float[] a) => Utterance?.Invoke(a);
    }

    /// <summary>Says "words N" for audio N samples long, after an optional delay that a cancel cuts short.</summary>
    sealed class CountingStt : ISpeechToText
    {
        public readonly List<int> Calls = new();
        public TimeSpan Delay;
        public bool Throw;
        public int Cancelled;
        public async Task<string> TranscribeAsync(float[] audio, CancellationToken ct)
        {
            lock (Calls) Calls.Add(audio.Length);
            if (Throw) throw new InvalidOperationException("model hiccup");
            try { if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            return $"words {audio.Length}";
        }
    }

    static Engine NewEngine(List<EngineEvent> events)
    {
        var e = new Engine(new EngineOptions { AutoSuggest = false }, new FakeSuggester(), new FakeCapture());
        e.Event += ev => { lock (events) events.Add(ev); };
        return e;
    }

    static void WaitUntil(Func<bool> cond, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); Thread.Sleep(10); }
    }

    static AudioPipeline Pipe(Engine engine, ISpeechToText stt, Mic mic, TimeSpan? gap = null)
    {
        var pipe = new AudioPipeline(engine, Task.FromResult(stt)) { PreviewMinGap = gap ?? TimeSpan.FromMilliseconds(20) };
        pipe.Add(Speaker.Them, mic);
        pipe.Start();
        return pipe;
    }

    [Fact]
    public void Words_appear_while_someone_is_speaking_and_the_real_transcript_replaces_them()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new CountingStt();
        var mic = new Mic();
        using var pipe = Pipe(engine, stt, mic);

        mic.Speak(true);
        mic.Preview(new float[1000]);
        WaitUntil(() => engine.LiveText(Speaker.Them) == "words 1000");

        mic.Preview(new float[2500]);
        WaitUntil(() => engine.LiveText(Speaker.Them) == "words 2500");

        mic.Speak(false);                                   // the pause that ends the sentence
        Assert.Equal("words 2500", engine.LiveText(Speaker.Them));   // the words stay until the real transcript is ready
        mic.Finish(new float[3000]);
        WaitUntil(() => engine.LiveText(Speaker.Them) is null);

        lock (events)
        {
            var turn = events.Single(e => e.Kind == EngineEventKind.Turn);
            Assert.Equal("words 3000", turn.Text);
            // The turn appears before the preview goes, so the words never vanish for a moment.
            Assert.True(events.IndexOf(turn) < events.FindLastIndex(e => e.Kind == EngineEventKind.Live && e.Text is null));
        }
    }

    [Fact]
    public void Previews_come_no_faster_than_the_speech_model_can_afford()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new CountingStt { Delay = TimeSpan.FromMilliseconds(150) };
        var mic = new Mic();
        using var pipe = Pipe(engine, stt, mic, gap: TimeSpan.FromMilliseconds(100));

        mic.Speak(true);
        for (int i = 1; i <= 20; i++) { mic.Preview(new float[i * 100]); Thread.Sleep(20); }   // 20 previews in 0.4 s
        Thread.Sleep(500);
        // A pass takes 150 ms and the next may not start until that long after: far fewer passes than previews.
        Assert.InRange(stt.Calls.Count, 1, 8);
    }

    [Fact]
    public void A_finished_utterance_cuts_a_preview_short_and_goes_first()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new CountingStt { Delay = TimeSpan.FromMilliseconds(600) };
        var mic = new Mic();
        using var pipe = Pipe(engine, stt, mic);

        mic.Speak(true);
        mic.Preview(new float[1000]);
        WaitUntil(() => stt.Calls.Count == 1);               // the preview pass is under way
        stt.Delay = TimeSpan.Zero;
        mic.Speak(false);
        mic.Finish(new float[2000]);

        WaitUntil(() => { lock (events) return events.Any(e => e.Kind == EngineEventKind.Turn); }, 2000);   // far less than the preview's 600 ms
        Assert.Equal(1, stt.Cancelled);
        Assert.Null(engine.LiveText(Speaker.Them));
    }

    [Fact]
    public void A_failing_preview_is_ignored_but_the_real_transcription_still_runs()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new CountingStt { Throw = true };
        var mic = new Mic();
        using var pipe = Pipe(engine, stt, mic);

        mic.Speak(true);
        mic.Preview(new float[1000]);
        WaitUntil(() => stt.Calls.Count == 1);
        Thread.Sleep(100);
        Assert.Null(engine.LiveText(Speaker.Them));
        Assert.DoesNotContain(events, e => e.Kind == EngineEventKind.Error);   // no alarm for a mere preview

        stt.Throw = false;
        mic.Speak(false);
        mic.Finish(new float[2000]);
        WaitUntil(() => { lock (events) return events.Any(e => e.Kind == EngineEventKind.Turn); });
    }

    [Fact]
    public void Noise_that_transcribes_to_nothing_leaves_no_words_behind()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new SilentStt();
        var mic = new Mic();
        using var pipe = Pipe(engine, stt, mic);

        mic.Speak(true);
        mic.Preview(new float[1000]);
        mic.Speak(false);
        mic.Finish(new float[2000]);
        WaitUntil(() => stt.Calls >= 1);
        Thread.Sleep(100);
        Assert.Null(engine.LiveText(Speaker.Them));
        Assert.DoesNotContain(events, e => e.Kind == EngineEventKind.Turn);
    }

    sealed class SilentStt : ISpeechToText
    {
        public int Calls;
        public Task<string> TranscribeAsync(float[] audio, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult("[BLANK_AUDIO]"); }
    }

    [Fact]
    public void New_speech_clears_the_previous_words_so_old_text_is_never_shown_as_new()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new CountingStt();
        var mic = new Mic();
        using var pipe = Pipe(engine, stt, mic);

        mic.Speak(true);
        mic.Preview(new float[1000]);
        WaitUntil(() => engine.LiveText(Speaker.Them) == "words 1000");
        mic.Speak(false);
        mic.Speak(true);                                       // the next sentence starts before the last one's transcript is back
        Assert.Null(engine.LiveText(Speaker.Them));
        Assert.True(engine.IsHearingSpeech);
    }
}
