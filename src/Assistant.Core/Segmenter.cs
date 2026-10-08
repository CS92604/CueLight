namespace Assistant.Core;

/// <summary>
/// Energy-based voice activity detector that cuts a mono 16 kHz stream into utterances.
/// Feed it chunks of any size; finished utterances come back as float arrays.
/// </summary>
public sealed class Segmenter
{
    private readonly int _frame, _frameMs, _silenceFrames, _minSpeechFrames, _maxFrames;
    private readonly float _minRms, _noiseFactor;
    private readonly int _prerollFrames;
    private readonly Queue<float[]> _preroll = new();
    private readonly List<float> _pending = new();
    private readonly List<float[]> _active = new();
    private float _noise;
    private int _speech, _silence;
    private bool _wasSpeaking;

    /// <summary>True once enough speech has been heard to be worth transcribing (a click or a cough shorter than
    /// that never counts), until the pause that ends it. A short pause between words doesn't end it.</summary>
    public bool IsSpeaking => _active.Count > 0 && _speech >= _minSpeechFrames;

    /// <summary>How many 30 ms stretches of actual voice the speech in progress has had so far (pauses don't count).</summary>
    public int VoicedFrames => _active.Count > 0 ? _speech : 0;

    /// <summary>How many 30 ms stretches of quiet there have been since the last voice (0 while nobody is speaking).</summary>
    public int TrailingSilenceFrames => _active.Count > 0 ? _silence : 0;

    /// <summary>The audio of the speech in progress (everything since it began, at most the latest
    /// <paramref name="maxSeconds"/>), for a live preview. Null when nobody is speaking.</summary>
    public float[]? SnapshotSpeech(double maxSeconds = 25)
    {
        if (!IsSpeaking) return null;
        int keep = Math.Max(1, (int)(maxSeconds * 1000 / _frameMs));
        int skip = Math.Max(0, _active.Count - keep);
        var result = new float[(_active.Count - skip) * _frame];
        int pos = 0;
        for (int i = skip; i < _active.Count; i++) { _active[i].CopyTo(result, pos); pos += _frame; }
        return result;
    }

    /// <summary>Raised when <see cref="IsSpeaking"/> changes. Runs on the thread that feeds the segmenter.</summary>
    public event Action<bool>? SpeakingChanged;

    private void NotifySpeaking()
    {
        bool now = IsSpeaking;
        if (now == _wasSpeaking) return;
        _wasSpeaking = now;
        SpeakingChanged?.Invoke(now);
    }

    public Segmenter(int sampleRate = 16000, int frameMs = 30, int silenceMs = 700, int minSpeechMs = 300,
        double maxUtteranceS = 25, int prerollMs = 240, float minRms = 0.008f, float noiseFactor = 3f)
    {
        _frame = sampleRate * frameMs / 1000;
        _frameMs = frameMs;
        _silenceFrames = Math.Max(1, silenceMs / frameMs);
        _minSpeechFrames = Math.Max(1, minSpeechMs / frameMs);
        _maxFrames = (int)(maxUtteranceS * 1000 / frameMs);
        _prerollFrames = Math.Max(1, prerollMs / frameMs);
        _minRms = minRms;
        _noiseFactor = noiseFactor;
    }

    public IReadOnlyList<float[]> Feed(ReadOnlySpan<float> samples)
    {
        var output = new List<float[]>();
        _pending.AddRange(samples.ToArray());
        int frames = _pending.Count / _frame;
        for (int i = 0; i < frames; i++)
        {
            var frame = _pending.GetRange(i * _frame, _frame).ToArray();
            var utt = Step(frame);
            if (utt is not null) output.Add(utt);
        }
        _pending.RemoveRange(0, frames * _frame);
        return output;
    }

    /// <summary>Whatever speech is buffered (call at shutdown).</summary>
    public float[]? Flush()
    {
        float[]? utt = null;
        if (_active.Count > 0 && _speech >= _minSpeechFrames) utt = Concat(_active);
        Reset();
        NotifySpeaking();
        return utt;
    }

    private void Reset()
    {
        _active.Clear();
        _speech = _silence = 0;
    }

    private float[]? Step(float[] frame)
    {
        var utterance = StepCore(frame);
        NotifySpeaking();
        return utterance;
    }

    private float[]? StepCore(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        float rms = (float)Math.Sqrt(sum / frame.Length);
        float threshold = Math.Max(_minRms, _noise * _noiseFactor);
        bool speaking = rms > threshold;

        if (_active.Count == 0)
        {
            if (speaking)
            {
                _active.AddRange(_preroll);
                _preroll.Clear();
                _active.Add(frame);
                _speech = 1;
                _silence = 0;
            }
            else
            {
                // Track the noise floor only while nobody is talking.
                _noise = _noise == 0f ? rms : 0.95f * _noise + 0.05f * rms;
                _preroll.Enqueue(frame);
                while (_preroll.Count > _prerollFrames) _preroll.Dequeue();
            }
            return null;
        }

        _active.Add(frame);
        if (speaking) { _speech++; _silence = 0; } else _silence++;
        if (_silence >= _silenceFrames || _active.Count >= _maxFrames)
        {
            var utt = _speech >= _minSpeechFrames ? Concat(_active) : null;
            Reset();
            return utt;
        }
        return null;
    }

    private static float[] Concat(List<float[]> frames)
    {
        var result = new float[frames.Sum(f => f.Length)];
        int pos = 0;
        foreach (var f in frames) { f.CopyTo(result, pos); pos += f.Length; }
        return result;
    }
}

/// <summary>Streaming downmix + resample of interleaved audio to mono 16 kHz (area averaging).</summary>
public sealed class StreamResampler
{
    private readonly int _channels;
    private readonly double _ratio; // input samples per output sample
    private readonly List<float> _buffer = new();
    private double _pos;

    public StreamResampler(int inputRate, int channels, int outputRate = 16000)
    {
        _channels = Math.Max(1, channels);
        _ratio = (double)inputRate / outputRate;
    }

    public float[] Process(ReadOnlySpan<float> interleaved)
    {
        for (int i = 0; i + _channels <= interleaved.Length; i += _channels)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += interleaved[i + c];
            _buffer.Add(sum / _channels);
        }

        var output = new List<float>();
        while (_pos + _ratio <= _buffer.Count)
        {
            // Average the input interval [pos, pos + ratio), weighting partial samples at the edges.
            double start = _pos, end = _pos + _ratio, acc = 0;
            for (int i = (int)start; i < Math.Ceiling(end) && i < _buffer.Count; i++)
            {
                double w = Math.Min(i + 1, end) - Math.Max(i, start);
                acc += _buffer[i] * w;
            }
            output.Add((float)(acc / _ratio));
            _pos += _ratio;
        }

        int consumed = (int)_pos;
        if (consumed > 0)
        {
            _buffer.RemoveRange(0, Math.Min(consumed, _buffer.Count));
            _pos -= consumed;
        }
        return output.ToArray();
    }
}
