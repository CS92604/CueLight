using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Assistant.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Assistant.App.Platform;

/// <summary>
/// Captures either what the speakers are playing (loopback: the other people on the call) or the
/// default microphone, and yields finished utterances. Follows the default device if it changes
/// (for example when headphones are plugged in).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioSource : IAudioSource, IMMNotificationClient
{
    private readonly bool _loopback;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _lock = new();
    private IWaveIn? _capture;
    private AudioIngest? _ingest;
    private Timer? _tick;
    private Timer? _restart;
    private bool _disposed;

    public event Action<float[]>? Utterance;
    public event Action<string>? Failed;

    public WasapiAudioSource(bool loopback) => _loopback = loopback;

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        Open();
    }

    private void Open()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Close();
            try
            {
                IWaveIn capture = _loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
                var format = capture.WaveFormat;
                var ingest = new AudioIngest(format.SampleRate, format.Channels, a => Utterance?.Invoke(a));
                capture.DataAvailable += (_, e) => OnData(ingest, format, e.Buffer, e.BytesRecorded);
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null && !_disposed)
                        Failed?.Invoke(_loopback
                            ? "Lost the audio output device. Switch your output device and it will reconnect."
                            : "Lost the microphone.");
                };
                capture.StartRecording();
                _capture = capture;
                _ingest = ingest;
                _tick = new Timer(_ => ingest.Tick(), null, 100, 100);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(_loopback
                    ? $"Couldn't listen to your speakers: {ex.Message}"
                    : $"Couldn't open the microphone: {ex.Message}");
            }
        }
    }

    private void Close()
    {
        _tick?.Dispose();
        _tick = null;
        _ingest?.Flush();
        try { _capture?.StopRecording(); } catch { /* device already gone */ }
        _capture?.Dispose();
        _capture = null;
        _ingest = null;
    }

    private static void OnData(AudioIngest ingest, WaveFormat format, byte[] buffer, int count)
    {
        float[] samples;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32 && IsFloat(format)))
        {
            samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, count - count % 4)).ToArray();
        }
        else if (format.BitsPerSample == 16)
        {
            var s16 = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, count - count % 2));
            samples = new float[s16.Length];
            for (int i = 0; i < samples.Length; i++) samples[i] = s16[i] / 32768f;
        }
        else if (format.BitsPerSample == 32)
        {
            var s32 = MemoryMarshal.Cast<byte, int>(buffer.AsSpan(0, count - count % 4));
            samples = new float[s32.Length];
            for (int i = 0; i < samples.Length; i++) samples[i] = s32[i] / 2147483648f;
        }
        else return; // 24-bit shared-mode capture is not offered by Windows' audio engine
        ingest.Feed(samples);
    }

    private static bool IsFloat(WaveFormat format) =>
        format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat;

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // -- follow the default device -----------------------------------------------------------

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role != Role.Multimedia) return;
        if ((_loopback && flow == DataFlow.Render) || (!_loopback && flow == DataFlow.Capture))
        {
            _restart?.Dispose();
            _restart = new Timer(_ => Open(), null, 500, Timeout.Infinite); // debounce the burst of change events
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _restart?.Dispose();
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            Close();
        }
        _enumerator.Dispose();
    }
}
