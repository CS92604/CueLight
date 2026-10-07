using Assistant.Core;

namespace Assistant.Tests;

public class SpeechActivityTests
{
    const int Rate = 16000;
    static float[] Tone(double seconds, float amp = 0.2f) =>
        Enumerable.Range(0, (int)(Rate * seconds)).Select(i => amp * (float)Math.Sin(2 * Math.PI * 220 * i / Rate)).ToArray();
    static float[] Silence(double seconds) => new float[(int)(Rate * seconds)];

    static void Feed(Segmenter seg, params float[][] parts)
    {
        var all = parts.SelectMany(p => p).ToArray();
        for (int i = 0; i < all.Length; i += 1600) seg.Feed(all.AsSpan(i, Math.Min(1600, all.Length - i)));
    }

    [Fact]
    public void Speech_is_announced_when_it_starts_and_when_the_pause_ends_it()
    {
        var seg = new Segmenter();
        var changes = new List<bool>();
        seg.SpeakingChanged += changes.Add;

        Feed(seg, Silence(0.5));
        Assert.Empty(changes);
        Assert.False(seg.IsSpeaking);

        Feed(seg, Tone(1.0));
        Assert.Equal(new[] { true }, changes);
        Assert.True(seg.IsSpeaking);

        Feed(seg, Silence(0.3));              // a breath between words: still speaking
        Assert.Equal(new[] { true }, changes);

        Feed(seg, Silence(0.8));              // a real pause ends it
        Assert.Equal(new[] { true, false }, changes);
        Assert.False(seg.IsSpeaking);
    }

    [Fact]
    public void A_click_or_a_cough_is_not_announced_as_speech()
    {
        var seg = new Segmenter();
        var changes = new List<bool>();
        seg.SpeakingChanged += changes.Add;
        Feed(seg, Silence(1), Tone(0.1), Silence(1.5));
        Assert.Empty(changes);
    }

    [Fact]
    public void A_whole_utterance_inside_one_chunk_is_still_announced_in_order()
    {
        var seg = new Segmenter();
        var changes = new List<bool>();
        seg.SpeakingChanged += changes.Add;
        var all = Silence(0.5).Concat(Tone(1.0)).Concat(Silence(1.2)).ToArray();
        seg.Feed(all);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Flushing_ends_the_speech()
    {
        var seg = new Segmenter();
        var changes = new List<bool>();
        seg.SpeakingChanged += changes.Add;
        Feed(seg, Silence(0.3), Tone(1.0));
        seg.Flush();
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void The_device_level_passes_it_on()
    {
        var changes = new List<bool>();
        var ingest = new AudioIngest(Rate, 1, _ => { });
        ingest.SpeakingChanged += changes.Add;
        ingest.Feed(Silence(0.3));
        ingest.Feed(Tone(1.0));
        Assert.Equal(new[] { true }, changes);
        ingest.Flush();
        Assert.Equal(new[] { true, false }, changes);
    }
}

public class EngineHearingTests
{
    static Engine NewEngine(List<EngineEvent> events)
    {
        var e = new Engine(new EngineOptions { AutoSuggest = false }, new FakeSuggester(), new FakeCapture());
        e.Event += ev => { lock (events) events.Add(ev); };
        return e;
    }

    [Fact]
    public void Hearing_is_announced_once_per_change_and_lasts_until_everyone_stops()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        Assert.False(engine.IsHearingSpeech);

        engine.SetSpeaking(Speaker.Them, true);
        engine.SetSpeaking(Speaker.Them, true);      // no change
        Assert.True(engine.IsHearingSpeech);
        Assert.Single(events, e => e.Kind == EngineEventKind.Hearing);

        engine.SetSpeaking(Speaker.Me, true);        // you speak too
        engine.SetSpeaking(Speaker.Them, false);
        Assert.True(engine.IsHearingSpeech);          // still you
        engine.SetSpeaking(Speaker.Me, false);
        Assert.False(engine.IsHearingSpeech);
        Assert.Equal(2, events.Count(e => e.Kind == EngineEventKind.Hearing));
    }

    [Fact]
    public void Turning_recording_off_clears_it()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        engine.SetSpeaking(Speaker.Them, true);
        engine.SetPaused(true);
        Assert.False(engine.IsHearingSpeech);
        engine.SetSpeaking(Speaker.Them, true);       // late news from the device while paused
        Assert.False(engine.IsHearingSpeech);
        engine.SetPaused(false);
        Assert.True(engine.IsHearingSpeech);          // the device really is still hearing speech
    }

    sealed class TalkingSource : IAudioSource
    {
        public event Action<float[]>? Utterance { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public event Action<bool>? Speaking;
        public void Start() { }
        public void Dispose() { }
        public void Say(bool on) => Speaking?.Invoke(on);
    }

    sealed class NoStt : ISpeechToText
    {
        public Task<string> TranscribeAsync(float[] audio, CancellationToken ct) => Task.FromResult("");
    }

    [Fact]
    public void A_device_that_hears_speech_reaches_the_engine_with_who_is_speaking()
    {
        var events = new List<EngineEvent>();
        using var engine = NewEngine(events);
        var source = new TalkingSource();
        using var pipe = new AudioPipeline(engine, Task.FromResult<ISpeechToText>(new NoStt()));
        pipe.Add(Speaker.Them, source);
        pipe.Start();

        source.Say(true);
        Assert.True(engine.IsHearingSpeech);
        Assert.Equal(Speaker.Them, events.Last(e => e.Kind == EngineEventKind.Hearing).Speaker);
        source.Say(false);
        Assert.False(engine.IsHearingSpeech);
    }
}

public class ThinkingSettingTests
{
    static async Task<System.Text.Json.JsonElement> Body(string model, bool thinkFirst = false)
    {
        using var server = new FakeClaudeServer();
        var s = new ClaudeSuggester(() => "k", baseUrl: server.Url);
        var req = new SuggestionRequest("Them: hello there friend", new Settings { Model = model, ThinkFirst = thinkFirst }, Trigger.Speech);
        await foreach (var _ in s.StreamAsync(req, CancellationToken.None)) { }
        return Assert.Single(server.Requests).Body;
    }

    [Fact]
    public async Task Sonnet_replies_without_thinking_first_by_default()
    {
        var body = await Body("claude-sonnet-5-5");
        Assert.Equal("between_tools", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Haiku_5_5_replies_without_thinking_first_by_default()
    {
        var body = await Body("claude-haiku-5-5");
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Theory]
    [InlineData("claude-opus-5-5")]      // can't have thinking turned off
    [InlineData("claude-haiku-4-5")]     // doesn't think by default
    public async Task Other_models_are_sent_no_thinking_setting(string model)
    {
        var body = await Body(model);
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-haiku-5-5")]
    public async Task Think_before_replying_leaves_thinking_on_and_raises_the_effort(string model)
    {
        var body = await Body(model, thinkFirst: true);
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("medium", body.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task If_the_setting_is_ever_refused_the_request_is_sent_again_without_it_and_remembered()
    {
        using var server = new FakeClaudeServer { RefuseThinkingSetting = true };
        var s = new ClaudeSuggester(() => "k", baseUrl: server.Url);
        var req = new SuggestionRequest("Them: hello there friend", new Settings { Model = "claude-sonnet-5-5" }, Trigger.Speech);

        var text = new System.Text.StringBuilder();
        await foreach (var chunk in s.StreamAsync(req, CancellationToken.None)) text.Append(chunk);
        Assert.Contains("Works for me!", text.ToString());
        Assert.Equal(2, server.Requests.Count);
        Assert.True(server.Requests[0].Body.TryGetProperty("thinking", out _));
        Assert.False(server.Requests[1].Body.TryGetProperty("thinking", out _));

        await foreach (var _ in s.StreamAsync(req, CancellationToken.None)) { }
        Assert.Equal(3, server.Requests.Count);       // one request this time: it didn't try the refused setting again
        Assert.False(server.Requests[2].Body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task Other_refusals_are_not_retried()
    {
        using var server = new FakeClaudeServer { Status = 400 };
        var s = new ClaudeSuggester(() => "k", baseUrl: server.Url);
        var req = new SuggestionRequest("Them: hello", new Settings { Model = "claude-sonnet-5-5" }, Trigger.Speech);
        await Assert.ThrowsAnyAsync<Exception>(async () => { await foreach (var _ in s.StreamAsync(req, CancellationToken.None)) { } });
    }

    [Fact]
    public void Think_before_replying_is_off_unless_asked_for_and_is_saved()
    {
        Assert.False(new Settings().ThinkFirst);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = new SettingsStore(dir);
        store.Save(new Settings { ThinkFirst = true });
        Assert.True(new SettingsStore(dir).Load().ThinkFirst);
    }

    [Fact]
    public void A_response_starts_sooner_because_the_engine_waits_less_before_asking()
    {
        Assert.True(new EngineOptions().Debounce <= TimeSpan.FromMilliseconds(500));
    }
}
