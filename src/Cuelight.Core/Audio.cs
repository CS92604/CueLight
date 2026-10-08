using System.Text.RegularExpressions;

namespace Cuelight.Core;

/// <summary>A device that yields finished utterances of mono 16 kHz audio.</summary>
public interface IAudioSource : IDisposable
{
    event Action<float[]>? Utterance;
    event Action<string>? Failed;

    /// <summary>The device works again after a <see cref="Failed"/> (it was unplugged, the PC slept, ...).</summary>
    event Action? Recovered { add { } remove { } }

    /// <summary>Someone started (true) or stopped (false) speaking on this device. Optional: a source that
    /// can't tell just never raises it.</summary>
    event Action<bool>? Speaking { add { } remove { } }

    /// <summary>While someone is speaking: all of the speech so far, about once a second, so the words can be shown
    /// before the sentence is over. Optional.</summary>
    event Action<float[]>? Partial { add { } remove { } }

    void Start();
}

public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct);

    /// <summary>
    /// A quicker pass for the live words, used while someone is still speaking; the real transcript replaces it.
    /// An engine with no cheaper way to do it just transcribes.
    /// </summary>
    Task<string> PreviewAsync(float[] audio16k, CancellationToken ct) => TranscribeAsync(audio16k, ct);
}

/// <summary>
/// Raw device audio in, utterances out: downmix + resample to 16 kHz, then voice-activity split.
/// Windows' loopback capture stops delivering data while nothing is playing, which would leave the
/// last utterance "open" forever; <see cref="Tick"/> injects silence after a quiet gap to close it.
/// </summary>
public sealed class AudioIngest
{
    private readonly StreamResampler _resampler;
    private readonly Segmenter _segmenter = new();
    private readonly Action<float[]> _onUtterance;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private double _lastData;

    /// <summary>Speech started (true) or ended (false) on this device.</summary>
    public event Action<bool>? SpeakingChanged;

    /// <summary>While speech goes on: the speech so far, as soon as there is enough to be speech (about 0.3 s) and then
    /// every fifth of a second, plus once more when a pause begins. (The listener only ever works on the newest.)</summary>
    public event Action<float[]>? Partial;

    // Voiced 30 ms frames of new speech needed before the next preview, and how much quiet after speech counts as
    // the start of a pause (a last look, so the final words show before the finished transcript is ready).
    private const int FirstPartialFrames = 10, PartialFrames = 7, PauseLookFrames = 5;
    private int _voicedAtLastPartial, _framesForNextPartial = FirstPartialFrames;

    public AudioIngest(int sampleRate, int channels, Action<float[]> onUtterance, Func<double>? clock = null)
    {
        _segmenter.SpeakingChanged += on => SpeakingChanged?.Invoke(on);
        _resampler = new StreamResampler(sampleRate, channels);
        _onUtterance = onUtterance;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed.TotalSeconds);
        _lastData = _clock();
    }

    public void Feed(ReadOnlySpan<float> interleaved)
    {
        lock (_lock)
        {
            _lastData = _clock();
            Push(_resampler.Process(interleaved));
        }
    }

    /// <summary>Call every ~100 ms. If the device has gone quiet, feed silence so speech can end.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (_clock() - _lastData < 0.25) return;
            Push(new float[1600]); // 100 ms of silence at 16 kHz
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_segmenter.Flush() is { } tail) _onUtterance(tail);
        }
    }

    private void Push(float[] mono16k)
    {
        foreach (var utt in _segmenter.Feed(mono16k)) _onUtterance(utt);

        if (!_segmenter.IsSpeaking)
        {
            _voicedAtLastPartial = 0;
            _framesForNextPartial = FirstPartialFrames;
            return;
        }
        // Counting frames of real voice (not time) means a trailing pause never triggers another preview of the same words.
        int voiced = _segmenter.VoicedFrames;
        if (voiced < _voicedAtLastPartial) _voicedAtLastPartial = 0;   // a new sentence began inside this chunk
        int fresh = voiced - _voicedAtLastPartial;
        bool pauseBegan = fresh > 0 && _segmenter.TrailingSilenceFrames >= PauseLookFrames;
        if (fresh < _framesForNextPartial && !pauseBegan) return;
        _voicedAtLastPartial = voiced;
        _framesForNextPartial = PartialFrames;
        if (_segmenter.SnapshotSpeech() is { } speech) Partial?.Invoke(speech);
    }
}

/// <summary>Removes the non-speech annotations Whisper emits for noise, music and silence.</summary>
public static partial class TranscriptCleaner
{
    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]|[♪♫🎵]+")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Phrases Whisper invents out of near-silence and music (it was trained on video subtitles).
    [GeneratedRegex(@"thank(s| you) for watching[.!]?|subtitles? by the amara\.org community|(please (like and )?|like and )subscribe( to (my|the|our) channel)?[.!]?|subscribe to (my|the|our) channel[.!]?|transcribed by [\w .]+?(?=[.!]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Invented();

    public static string Clean(string text)
    {
        var t = Spaces().Replace(Invented().Replace(Annotation().Replace(text, " "), " "), " ").Trim();
        // Only punctuation left (e.g. "..." or "-"): nothing was actually said.
        return t.Any(char.IsLetterOrDigit) ? t : "";
    }
}

/// <summary>
/// Transcribes utterances one at a time and feeds them to the engine. Utterances that arrive
/// while the speech model is still loading wait (a small backlog is kept, oldest dropped).
///
/// It also keeps a live preview: while someone is speaking, the speech so far is run through the speech model's
/// quick preview pass again and again (whenever the speech model has nothing more important to do) and handed to
/// the engine as <see cref="Engine.SetLive"/>, so the words show up as they are said. A finished utterance always
/// goes first, and cuts a preview short. The preview is replaced by the real transcript when that is ready.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private readonly Engine _engine;
    private readonly Task<ISpeechToText> _stt;
    private readonly System.Threading.Channels.Channel<(Speaker Who, float[] Audio)> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IAudioSource> _sources = new();
    private readonly Func<DateTime> _now;
    private DateTime _lastBehindNotice = DateTime.MinValue;

    // Live preview state (guarded by _live): the newest speech audio per speaker not yet previewed, who is
    // speaking, the preview pass in progress, and the earliest time the next pass may start.
    private readonly object _live = new();
    private readonly Dictionary<Speaker, float[]> _partials = new();
    private readonly HashSet<Speaker> _speaking = new();
    private CancellationTokenSource? _partialCts;
    private DateTime _partialAllowedAfter = DateTime.MinValue;
    private readonly SemaphoreSlim _work = new(0);

    /// <summary>The least rest between two preview passes. After a slow pass the rest is longer (a third of how long
    /// the pass took), so on a slow PC the preview leaves the speech model some time for everything else, while on
    /// a quick one the next pass follows right behind the last.</summary>
    public TimeSpan PreviewMinGap { get; init; } = TimeSpan.FromMilliseconds(40);

    private const double RestAfterPass = 0.33;

    /// <summary>Shown when speech arrives faster than this PC can transcribe it and the oldest is skipped.</summary>
    public const string FallingBehind =
        "Speech recognition can't keep up on this PC, so some speech was skipped. Choose Fast under Settings, Speech recognition.";

    public AudioPipeline(Engine engine, Task<ISpeechToText> stt, Func<DateTime>? now = null)
    {
        _engine = engine;
        _stt = stt;
        _now = now ?? (() => DateTime.UtcNow);
        _queue = System.Threading.Channels.Channel.CreateBounded<(Speaker, float[])>(
            new System.Threading.Channels.BoundedChannelOptions(8) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest },
            _ => NoteDropped());
    }

    private void NoteDropped()
    {
        // The bounded queue overflowed: transcription is slower than real time. Say so, at most once a minute.
        var now = _now();
        lock (_sources)
        {
            if (now - _lastBehindNotice < TimeSpan.FromMinutes(1)) return;
            _lastBehindNotice = now;
        }
        _engine.RaiseError(FallingBehind);
    }

    private bool _started;

    /// <summary>Attach a device. If the pipeline is already running the device starts right away.</summary>
    public void Add(Speaker who, IAudioSource source)
    {
        string? lastFailure = null;
        source.Utterance += audio =>
        {
            if (!_queue.Writer.TryWrite((who, audio))) return;
            lock (_live) _partialCts?.Cancel();   // a finished utterance goes before any preview
            _work.Release();
        };
        source.Speaking += on =>
        {
            lock (_live)
            {
                if (on) _speaking.Add(who);
                else { _speaking.Remove(who); _partials.Remove(who); }
            }
            _engine.SetSpeaking(who, on);
            if (on) _engine.SetLive(who, null);   // new speech: whatever preview was showing is old
        };
        source.Partial += audio =>
        {
            lock (_live)
            {
                if (!_speaking.Contains(who)) return;
                _partials[who] = audio;
            }
            _work.Release();
        };
        source.Failed += msg => { lastFailure = msg; _engine.RaiseError(msg); };
        source.Recovered += () => { if (lastFailure is { } failed) _engine.RaiseRecovered(failed); lastFailure = null; };
        lock (_sources) _sources.Add(source);
        if (_started) source.Start();
    }

    /// <summary>Stop and release a device attached with <see cref="Add"/>.</summary>
    public void Remove(IAudioSource source)
    {
        lock (_sources) _sources.Remove(source);
        source.Dispose();
    }

    public void Start()
    {
        _started = true;
        _ = Task.Run(LoopAsync);
        IAudioSource[] sources;
        lock (_sources) sources = _sources.ToArray();
        foreach (var s in sources) s.Start();
    }

    private async Task LoopAsync()
    {
        ISpeechToText stt;
        try { stt = await _stt.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _engine.RaiseError($"Couldn't load the speech model: {ex.Message}"); return; }

        try
        {
            while (true)
            {
                await _work.WaitAsync(_cts.Token);
                while (_queue.Reader.TryRead(out var item)) await TranscribeAsync(stt, item.Who, item.Audio);
                await PreviewAsync(stt);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TranscribeAsync(ISpeechToText stt, Speaker who, float[] audio)
    {
        try
        {
            var text = TranscriptCleaner.Clean(await stt.TranscribeAsync(audio, _cts.Token));
            if (text.Length > 0) _engine.AddTurn(who, text);
            // The preview was only ever a stand-in for this: it goes now, unless new speech has begun since.
            bool speakingAgain;
            lock (_live) speakingAgain = _speaking.Contains(who);
            if (!speakingAgain) _engine.SetLive(who, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _engine.RaiseError($"Transcription failed: {ex.Message}"); }
    }

    /// <summary>One live preview pass, if someone is speaking and the speech model has had a rest.</summary>
    private async Task PreviewAsync(ISpeechToText stt)
    {
        Speaker who = default;
        float[] audio = Array.Empty<float>();
        CancellationTokenSource cts = null!;
        TimeSpan? comeBackIn = null;
        lock (_live)
        {
            if (_partials.Count == 0) return;
            var now = _now();
            if (now < _partialAllowedAfter) comeBackIn = _partialAllowedAfter - now;   // too soon after the last pass: keep it, try again then
            else
            {
                (who, audio) = _partials.First();
                _partials.Remove(who);
                _partialCts = cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            }
        }
        if (comeBackIn is { } wait)
        {
            _ = Task.Delay(wait + TimeSpan.FromMilliseconds(5), _cts.Token).ContinueWith(t => { if (!t.IsCanceled) _work.Release(); });
            return;
        }

        var started = _now();
        string text;
        try { text = TranscriptCleaner.Clean(await stt.PreviewAsync(audio, cts.Token)); }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { return; }  // a finished utterance came in; it goes first
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return; }   // a preview is a nicety; the real transcription reports real problems
        finally
        {
            lock (_live) { _partialCts = null; }
            cts.Dispose();
        }

        var took = _now() - started;
        var rest = TimeSpan.FromTicks((long)(took.Ticks * RestAfterPass));
        lock (_live) _partialAllowedAfter = _now() + (rest > PreviewMinGap ? rest : PreviewMinGap);
        bool stillRelevant;
        lock (_live) stillRelevant = _speaking.Contains(who);
        if (text.Length > 0 && stillRelevant) _engine.SetLive(who, text);
    }

    public void Dispose()
    {
        _cts.Cancel();
        IAudioSource[] sources;
        lock (_sources) sources = _sources.ToArray();
        foreach (var s in sources) s.Dispose();
    }
}
