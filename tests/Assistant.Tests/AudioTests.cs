using Assistant.Core;

namespace Assistant.Tests;

public class AudioIngestTests
{
    static float[] Stereo48k(double seconds, float amp)
    {
        int n = (int)(48000 * seconds);
        var d = new float[n * 2];
        for (int i = 0; i < n; i++) d[i * 2] = d[i * 2 + 1] = amp * (float)Math.Sin(2 * Math.PI * 220 * i / 48000);
        return d;
    }

    [Fact]
    public void Speech_then_silence_makes_one_utterance()
    {
        var got = new List<float[]>();
        double now = 0;
        var ingest = new AudioIngest(48000, 2, got.Add, () => now);
        ingest.Feed(Stereo48k(0.5, 0f));
        ingest.Feed(Stereo48k(1.0, 0.2f));
        ingest.Feed(Stereo48k(1.0, 0f));
        Assert.Single(got);
        Assert.InRange(got[0].Length / 16000.0, 1.0, 2.0);
    }

    [Fact]
    public void Loopback_going_quiet_still_ends_the_utterance()
    {
        // Windows loopback sends nothing while nothing plays: the device just stops delivering.
        var got = new List<float[]>();
        double now = 0;
        var ingest = new AudioIngest(48000, 2, got.Add, () => now);
        ingest.Feed(Stereo48k(0.5, 0f));
        ingest.Feed(Stereo48k(1.0, 0.2f));
        Assert.Empty(got);
        for (int i = 0; i < 20; i++) { now += 0.1; ingest.Tick(); }
        Assert.Single(got);
    }

    [Fact]
    public void Tick_does_nothing_while_audio_is_flowing()
    {
        var got = new List<float[]>();
        double now = 0;
        var ingest = new AudioIngest(48000, 2, got.Add, () => now);
        ingest.Feed(Stereo48k(1.0, 0.2f));
        now += 0.1; ingest.Tick();
        now += 0.1; ingest.Tick();
        Assert.Empty(got);
    }

    [Fact]
    public void Flush_returns_the_open_utterance()
    {
        var got = new List<float[]>();
        var ingest = new AudioIngest(48000, 2, got.Add);
        ingest.Feed(Stereo48k(0.3, 0f));
        ingest.Feed(Stereo48k(1.0, 0.2f));
        ingest.Flush();
        Assert.Single(got);
    }
}

public class TranscriptCleanerTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" (music) ", "")]
    [InlineData("♪ ♪", "")]
    [InlineData("...", "")]
    [InlineData("Hello there [music] how are you", "Hello there how are you")]
    [InlineData("  Can you start   Monday? ", "Can you start Monday?")]
    [InlineData("(laughs) That's funny.", "That's funny.")]
    [InlineData("Thanks for watching!", "")]
    [InlineData("Thank you for watching.", "")]
    [InlineData("Subtitles by the Amara.org community", "")]
    [InlineData("Please subscribe to my channel.", "")]
    [InlineData("Thanks for watching! See you next time", "See you next time")]
    [InlineData("Thank you.", "Thank you.")]            // could be said for real: kept
    [InlineData("I'd subscribe to that view.", "I'd subscribe to that view.")]
    public void Cleans_annotations(string input, string expected) => Assert.Equal(expected, TranscriptCleaner.Clean(input));
}

public class AudioPipelineTests
{
    sealed class FakeSource : IAudioSource
    {
        public event Action<float[]>? Utterance;
        public event Action<string>? Failed;
        public bool Started, Disposed;
        public void Start() => Started = true;
        public void Dispose() => Disposed = true;
        public void Say(float[] a) => Utterance?.Invoke(a);
        public void Fail(string m) => Failed?.Invoke(m);
    }

    sealed class FakeStt : ISpeechToText
    {
        public readonly Queue<string> Texts = new();
        public Task<string> TranscribeAsync(float[] audio, CancellationToken ct) => Task.FromResult(Texts.Dequeue());
    }

    static Engine NewEngine(List<EngineEvent> events)
    {
        var e = new Engine(new EngineOptions { AutoSuggest = false }, new FakeSuggester(), new FakeCapture());
        e.Event += ev => { lock (events) events.Add(ev); };
        return e;
    }

    static void WaitUntil(Func<bool> cond)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); Thread.Sleep(10); }
    }

    [Fact]
    public void Transcribed_speech_becomes_turns_and_noise_is_dropped()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var stt = new FakeStt();
        stt.Texts.Enqueue("[BLANK_AUDIO]");
        stt.Texts.Enqueue("Can you start Monday?");
        stt.Texts.Enqueue("Sure, nine works.");
        var them = new FakeSource();
        var me = new FakeSource();
        using var pipe = new AudioPipeline(engine, Task.FromResult<ISpeechToText>(stt));
        pipe.Add(Speaker.Them, them);
        pipe.Add(Speaker.Me, me);
        pipe.Start();
        Assert.True(them.Started && me.Started);

        them.Say(new float[10]);
        them.Say(new float[10]);
        WaitUntil(() => engine.Conversation.Render(1000).Contains("Monday"));
        me.Say(new float[10]);
        WaitUntil(() => engine.Conversation.Render(1000).Contains("nine"));
        Assert.Equal("Them: Can you start Monday?\nMe: Sure, nine works.", engine.Conversation.Render(1000));
    }

    [Fact]
    public void Audio_waits_for_the_speech_model_to_finish_loading()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var ready = new TaskCompletionSource<ISpeechToText>();
        var stt = new FakeStt();
        stt.Texts.Enqueue("Hello from the call.");
        var src = new FakeSource();
        using var pipe = new AudioPipeline(engine, ready.Task);
        pipe.Add(Speaker.Them, src);
        pipe.Start();
        src.Say(new float[10]);
        Thread.Sleep(200);
        Assert.True(engine.Conversation.IsEmpty);
        ready.SetResult(stt);
        WaitUntil(() => !engine.Conversation.IsEmpty);
    }

    [Fact]
    public void Device_and_model_failures_reach_the_user()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var src = new FakeSource();
        using var pipe = new AudioPipeline(engine, Task.FromException<ISpeechToText>(new IOException("download failed")));
        pipe.Add(Speaker.Them, src);
        pipe.Start();
        src.Fail("Audio device unplugged");
        WaitUntil(() => { lock (events) return events.Count(e => e.Kind == EngineEventKind.Error) >= 2; });
        lock (events)
        {
            Assert.Contains(events, e => e.Text == "Audio device unplugged");
            Assert.Contains(events, e => e.Text!.Contains("download failed"));
        }
    }
}
