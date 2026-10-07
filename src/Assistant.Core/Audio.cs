using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>A device that yields finished utterances of mono 16 kHz audio.</summary>
public interface IAudioSource : IDisposable
{
    event Action<float[]>? Utterance;
    event Action<string>? Failed;

    /// <summary>The device works again after a <see cref="Failed"/> (it was unplugged, the PC slept, ...).</summary>
    event Action? Recovered { add { } remove { } }

    void Start();
}

public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct);
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

    public AudioIngest(int sampleRate, int channels, Action<float[]> onUtterance, Func<double>? clock = null)
    {
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
    }
}

/// <summary>Removes the non-speech annotations Whisper emits for noise, music and silence.</summary>
public static partial class TranscriptCleaner
{
    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]|[♪♫🎵]+")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string Clean(string text)
    {
        var t = Spaces().Replace(Annotation().Replace(text, " "), " ").Trim();
        // Only punctuation left (e.g. "..." or "-"): nothing was actually said.
        return t.Any(char.IsLetterOrDigit) ? t : "";
    }
}

/// <summary>
/// Transcribes utterances one at a time and feeds them to the engine. Utterances that arrive
/// while the speech model is still loading wait (a small backlog is kept, oldest dropped).
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
    private Task? _loop;

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
        source.Utterance += audio => _queue.Writer.TryWrite((who, audio));
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
        _loop = Task.Run(LoopAsync);
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
            await foreach (var (who, audio) in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    var text = TranscriptCleaner.Clean(await stt.TranscribeAsync(audio, _cts.Token));
                    if (text.Length > 0) _engine.AddTurn(who, text);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _engine.RaiseError($"Transcription failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        IAudioSource[] sources;
        lock (_sources) sources = _sources.ToArray();
        foreach (var s in sources) s.Dispose();
    }
}
