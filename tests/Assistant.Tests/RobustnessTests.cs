using System.Runtime.CompilerServices;
using Anthropic.Exceptions;
using Assistant.Core;

namespace Assistant.Tests;

/// <summary>Keeps test runs from writing to the real log folder.</summary>
static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => AppLog.Directory = Path.Combine(Path.GetTempPath(), "cla-tests-log");
}

public class AppLogTests
{
    [Fact]
    public void Writes_lines_and_rolls_over_when_big()
    {
        var old = AppLog.Directory;
        var dir = Path.Combine(Path.GetTempPath(), "cla-log-" + Guid.NewGuid());
        AppLog.Directory = dir;
        try
        {
            AppLog.Info("hello there");
            AppLog.Error("it broke", new InvalidOperationException("boom"));
            var text = File.ReadAllText(AppLog.FilePath);
            Assert.Contains("hello there", text);
            Assert.Contains("ERROR it broke", text);
            Assert.Contains("boom", text);

            File.WriteAllText(AppLog.FilePath, new string('x', 600 * 1024));
            AppLog.Info("after the roll");
            Assert.True(File.Exists(Path.Combine(dir, "app.old.log")));
            Assert.Contains("after the roll", File.ReadAllText(AppLog.FilePath));
            Assert.True(new FileInfo(AppLog.FilePath).Length < 10_000);
        }
        finally { AppLog.Directory = old; }
    }

    [Fact]
    public void Logging_never_throws_even_when_the_folder_is_unusable()
    {
        var old = AppLog.Directory;
        var blocker = Path.Combine(Path.GetTempPath(), "cla-log-file-" + Guid.NewGuid());
        File.WriteAllText(blocker, "a file where the folder should be");
        AppLog.Directory = Path.Combine(blocker, "logs");
        try { AppLog.Warn("still fine"); AppLog.Error("x", new Exception("y")); }
        finally { AppLog.Directory = old; }
    }
}

public class PcmDecoderTests
{
    [Fact]
    public void Decodes_float_and_16_bit()
    {
        var f = BitConverter.GetBytes(0.5f).Concat(BitConverter.GetBytes(-0.25f)).ToArray();
        Assert.True(PcmDecoder.TryDecode(f, 32, true, out var a));
        Assert.Equal(new[] { 0.5f, -0.25f }, a);

        var s16 = BitConverter.GetBytes((short)16384).Concat(BitConverter.GetBytes((short)-32768)).ToArray();
        Assert.True(PcmDecoder.TryDecode(s16, 16, false, out var b));
        Assert.Equal(new[] { 0.5f, -1f }, b);
    }

    [Fact]
    public void Decodes_24_bit_with_sign()
    {
        // +0.5 = 0x400000, -0.5 = 0xC00000 (little-endian, three bytes each)
        byte[] bytes = { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 };
        Assert.True(PcmDecoder.TryDecode(bytes, 24, false, out var a));
        Assert.Equal(0.5, a[0], 5);
        Assert.Equal(-0.5, a[1], 5);
    }

    [Fact]
    public void Decodes_32_bit_integers()
    {
        var bytes = BitConverter.GetBytes(int.MinValue).Concat(BitConverter.GetBytes(1 << 30)).ToArray();
        Assert.True(PcmDecoder.TryDecode(bytes, 32, false, out var a));
        Assert.Equal(-1f, a[0]);
        Assert.Equal(0.5f, a[1]);
    }

    [Fact]
    public void Ignores_a_trailing_partial_sample_and_rejects_unknown_formats()
    {
        Assert.True(PcmDecoder.TryDecode(new byte[] { 0, 0, 0, 0, 7 }, 16, false, out var a));
        Assert.Equal(2, a.Length);
        Assert.True(PcmDecoder.TryDecode(new byte[] { 0, 0, 0, 0, 0 }, 32, true, out var b));
        Assert.Single(b);
        Assert.False(PcmDecoder.TryDecode(new byte[8], 8, false, out var c));
        Assert.Empty(c);
    }
}

public class ScreenWatcherTests
{
    sealed class FlakyCapture : IScreenCapture
    {
        public int FailFirst;
        public bool Black;
        public int Calls;
        public byte[] CapturePng(Region r) => new byte[] { 1 };
        public byte[] CaptureFullPng(Region r) => new byte[] { 1 };
        public (byte[] Bgra, int Width, int Height) GrabBgra(Region r)
        {
            int n = Interlocked.Increment(ref Calls);
            if (n <= FailFirst) throw new IOException("The handle is invalid");
            var px = new byte[4 * 4 * 4];
            if (!Black) Array.Fill(px, (byte)200);
            return (px, 4, 4);
        }
    }

    static void WaitUntil(Func<bool> cond, int ms = 4000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); Thread.Sleep(10); }
    }

    [Fact]
    public void Keeps_trying_through_failures_reports_once_and_says_when_it_works_again()
    {
        var capture = new FlakyCapture { FailFirst = 12 };
        var errors = new List<string>();
        int recovered = 0;
        using var w = new ScreenWatcher(new Region(0, 0, 4, 4), capture, () => { }, m => { lock (errors) errors.Add(m); },
            TimeSpan.FromMilliseconds(5), () => Interlocked.Increment(ref recovered), failuresBeforeError: 3);
        w.Start();
        WaitUntil(() => Volatile.Read(ref recovered) == 1);
        lock (errors)
        {
            var one = Assert.Single(errors);
            Assert.Contains("Still trying", one);
            Assert.Contains("handle is invalid", one);
        }
        Assert.True(capture.Calls > 12);
    }

    [Fact]
    public void A_short_hiccup_is_not_reported_at_all()
    {
        var capture = new FlakyCapture { FailFirst = 2 };
        int errors = 0, recovered = 0;
        using var w = new ScreenWatcher(new Region(0, 0, 4, 4), capture, () => { }, _ => Interlocked.Increment(ref errors),
            TimeSpan.FromMilliseconds(5), () => Interlocked.Increment(ref recovered), failuresBeforeError: 6);
        w.Start();
        WaitUntil(() => capture.Calls > 20);
        Assert.Equal(0, errors);
        Assert.Equal(0, recovered);
    }

    [Fact]
    public void Black_frames_from_a_locked_screen_are_skipped_without_noise()
    {
        var capture = new FlakyCapture { Black = true };
        int errors = 0, changes = 0;
        using var w = new ScreenWatcher(new Region(0, 0, 4, 4), capture, () => Interlocked.Increment(ref changes),
            _ => Interlocked.Increment(ref errors), TimeSpan.FromMilliseconds(5));
        w.Start();
        WaitUntil(() => capture.Calls > 20);
        Assert.Equal(0, errors);
        Assert.Equal(0, changes);
        Assert.False(w.Pending);
    }
}

public class AudioPipelineRobustnessTests
{
    sealed class Source : IAudioSource
    {
        public event Action<float[]>? Utterance;
        public event Action<string>? Failed;
        public event Action? Recovered;
        public void Start() { }
        public void Dispose() { }
        public void Say() => Utterance?.Invoke(new float[10]);
        public void Fail(string m) => Failed?.Invoke(m);
        public void Recover() => Recovered?.Invoke();
    }

    sealed class BlockedStt : ISpeechToText
    {
        public readonly ManualResetEventSlim Release = new();
        public Task<string> TranscribeAsync(float[] audio, CancellationToken ct) =>
            Task.Run(() => { Release.Wait(ct); return ""; }, ct);
    }

    static Engine NewEngine(Recorder rec)
    {
        var e = new Engine(new EngineOptions { AutoSuggest = false }, new FakeSuggester(), new FakeCapture());
        e.Event += rec.Add;
        return e;
    }

    [Fact]
    public void A_recovered_device_clears_exactly_the_message_it_caused()
    {
        var rec = new Recorder();
        using var engine = NewEngine(rec);
        var src = new Source();
        using var pipe = new AudioPipeline(engine, Task.FromResult<ISpeechToText>(new BlockedStt()));
        pipe.Add(Speaker.Them, src);
        src.Recover();                                    // nothing failed yet: nothing to clear
        Assert.Equal(0, rec.Count(EngineEventKind.Recovered));
        src.Fail("Lost the audio output device.");
        src.Recover();
        src.Recover();                                    // already cleared
        rec.WaitFor(EngineEventKind.Recovered);
        lock (rec.Events)
        {
            var e = Assert.Single(rec.Events, x => x.Kind == EngineEventKind.Recovered);
            Assert.Equal("Lost the audio output device.", e.Text);
        }
    }

    [Fact]
    public void Speech_arriving_faster_than_it_can_be_transcribed_is_reported_once_a_minute()
    {
        var rec = new Recorder();
        using var engine = NewEngine(rec);
        var stt = new BlockedStt();
        var now = DateTime.UtcNow;
        var src = new Source();
        using var pipe = new AudioPipeline(engine, Task.FromResult<ISpeechToText>(stt), () => now);
        pipe.Add(Speaker.Them, src);
        pipe.Start();
        for (int i = 0; i < 30; i++) src.Say();           // far more than the queue holds
        rec.WaitFor(EngineEventKind.Error);
        Thread.Sleep(100);
        Assert.Equal(1, rec.Count(EngineEventKind.Error)); // not once per dropped utterance

        now += TimeSpan.FromSeconds(61);
        for (int i = 0; i < 30; i++) src.Say();
        rec.WaitFor(EngineEventKind.Error, 2);
        lock (rec.Events) Assert.All(rec.Events.Where(e => e.Kind == EngineEventKind.Error), e => Assert.Equal(AudioPipeline.FallingBehind, e.Text));
        stt.Release.Set();
    }
}

public class StoreRobustnessTests
{
    [Fact]
    public void Settings_save_reports_failure_instead_of_throwing_and_leaves_no_temp_file()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "cla-set-" + Guid.NewGuid());
        File.WriteAllText(blocker, "not a folder");
        Assert.False(new SettingsStore(Path.Combine(blocker, "sub")).Save(new Settings()));

        var dir = Path.Combine(Path.GetTempPath(), "cla-set-" + Guid.NewGuid());
        Assert.True(new SettingsStore(dir).Save(new Settings()));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.True(new SettingsStore(dir).Exists);
    }

    [Fact]
    public void A_pc_with_few_cores_starts_on_the_fastest_speech_model()
    {
        Assert.Equal(SpeechAccuracy.Fast, Settings.ForFirstRun(2).SpeechAccuracy);
        Assert.Equal(SpeechAccuracy.Fast, Settings.ForFirstRun(4).SpeechAccuracy);
        Assert.Equal(SpeechAccuracy.Balanced, Settings.ForFirstRun(8).SpeechAccuracy);
    }

    [Fact]
    public void Api_key_save_leaves_no_temp_file_and_delete_is_quiet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cla-key-" + Guid.NewGuid());
        var store = new ApiKeyStore(dir, new UserOnlyFileProtector());
        store.Save("sk-ant-api03-abcdef123456");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Equal("sk-ant-api03-abcdef123456", store.Load());
        store.Delete();
        store.Delete(); // already gone
        Assert.False(store.HasKey);
    }
}

public class ErrorMessageTests
{
    [Fact]
    public void Network_trouble_gets_a_plain_message()
    {
        Assert.Contains("Couldn't reach", ClaudeSuggester.Describe(new HttpRequestException("No such host is known")));
        Assert.Contains("Couldn't reach", ClaudeSuggester.Describe(new AnthropicIOException("network", new HttpRequestException("x"))));
        Assert.Contains("too long", ClaudeSuggester.Describe(new TaskCanceledException()));
        Assert.Contains("too long", ClaudeSuggester.Describe(new TimeoutException()));
    }

    [Fact]
    public void A_broken_secure_connection_mentions_proxies_and_the_clock()
    {
        var ssl = new HttpRequestException("ssl", new System.Security.Authentication.AuthenticationException("The remote certificate is invalid"));
        var text = ClaudeSuggester.Describe(new AnthropicIOException("tls", ssl));
        Assert.Contains("secure connection", text);
        Assert.Contains("date and time", text);
    }

    [Fact]
    public void An_unknown_model_points_at_settings()
    {
        Assert.Contains("Settings", ClaudeSuggester.Describe(new AnthropicNotFoundException { StatusCode = System.Net.HttpStatusCode.NotFound, ResponseBody = "{}" }));
    }
}
