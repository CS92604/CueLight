using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Assistant.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Assistant.App.Platform;

/// <summary>
/// Captures either what the speakers are playing (loopback: the other people on the call) or the
/// default microphone, and yields finished utterances.
///
/// Sound devices come and go: headphones are plugged in, a Bluetooth headset drops out, the PC
/// sleeps and wakes. The source follows the default device, and when the device fails or there is
/// none it keeps retrying (2, 5, 10, then every 15 seconds) and says once what is wrong, and again
/// when it works. Everything runs off the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioSource : IAudioSource, IMMNotificationClient
{
    private static readonly int[] RetrySeconds = { 2, 5, 10, 15 };
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private readonly bool _loopback;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _lock = new();          // guards the capture objects; held while (re)opening
    private readonly object _stateLock = new();     // guards the failure/retry state; never held while waiting on a device
    private IWaveIn? _capture;
    private AudioIngest? _ingest;
    private Timer? _tick;
    private Timer? _restart;
    private volatile bool _disposed;
    private bool _failing;
    private int _attempt;
    private bool _loggedBadData;

    public event Action<float[]>? Utterance;
    public event Action<bool>? Speaking;
    public event Action<float[]>? Partial;
    public event Action<string>? Failed;
    public event Action? Recovered;

    public WasapiAudioSource(bool loopback) => _loopback = loopback;

    public void Start()
    {
        try { _enumerator.RegisterEndpointNotificationCallback(this); }
        catch (Exception ex) { AppLog.Error("Couldn't watch for audio device changes", ex); }
        ThreadPool.QueueUserWorkItem(_ => Open()); // device setup can be slow; keep it off the UI thread
    }

    private void Open()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Close();
            try
            {
                // NAudio opens the loopback device by its Multimedia role and the microphone by its Console role.
                if (!(_loopback ? _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                                : _enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)))
                    throw new NoDeviceException();

                IWaveIn capture = _loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
                var format = capture.WaveFormat;
                if (format.SampleRate <= 0 || format.Channels <= 0)
                {
                    capture.Dispose();
                    throw new NotSupportedException("the device reported an audio format this app can't read");
                }

                var ingest = new AudioIngest(format.SampleRate, format.Channels, a => Utterance?.Invoke(a));
                ingest.SpeakingChanged += on => Speaking?.Invoke(on);
                ingest.Partial += a => Partial?.Invoke(a);
                capture.DataAvailable += (_, e) => OnData(ingest, format, e.Buffer, e.BytesRecorded);
                capture.RecordingStopped += (_, e) =>
                {
                    // Runs on the capture thread while Close() may be waiting for it: no _lock here.
                    if (e.Exception is null || _disposed || !ReferenceEquals(Volatile.Read(ref _capture), capture)) return;
                    AppLog.Error(_loopback ? "Speaker capture stopped" : "Microphone capture stopped", e.Exception);
                    Fail(_loopback
                        ? "Lost the audio output device. Reconnecting as soon as it's back."
                        : "Lost the microphone. Reconnecting as soon as it's back.");
                };
                capture.StartRecording();
                Volatile.Write(ref _capture, capture);
                _ingest = ingest;
                _tick = new Timer(_ => ingest.Tick(), null, 100, 100);
                MarkWorking();
            }
            catch (Exception ex)
            {
                AppLog.Error(_loopback ? "Couldn't open the speakers" : "Couldn't open the microphone", ex);
                Fail(Describe(ex));
            }
        }
    }

    private void Close()
    {
        _tick?.Dispose();
        _tick = null;
        _ingest?.Flush();
        var capture = Interlocked.Exchange(ref _capture, null);
        try { capture?.StopRecording(); } catch { /* device already gone */ }
        try { capture?.Dispose(); } catch { /* ditto */ }
        _ingest = null;
    }

    private sealed class NoDeviceException : Exception { }

    private string Describe(Exception ex) => ex switch
    {
        NoDeviceException when _loopback =>
            "No speakers or headphones are available. Plug some in or turn them on and listening starts by itself.",
        NoDeviceException =>
            "No microphone found. Plug one in, or turn off “Include my microphone” in Settings.",
        UnauthorizedAccessException or COMException { HResult: E_ACCESSDENIED } when !_loopback =>
            "Windows is blocking microphone access. In Windows Settings, open Privacy & security, then Microphone, and allow desktop apps.",
        _ when _loopback => $"Couldn't listen to your speakers: {ex.Message} Trying again.",
        _ => $"Couldn't open the microphone: {ex.Message} Trying again.",
    };

    /// <summary>Report a problem once per streak, and retry with a growing delay.</summary>
    private void Fail(string message)
    {
        bool first;
        int delay;
        lock (_stateLock)
        {
            if (_disposed) return;
            first = !_failing;
            _failing = true;
            delay = RetrySeconds[Math.Min(_attempt++, RetrySeconds.Length - 1)];
            _restart?.Dispose();
            _restart = new Timer(_ => Open(), null, delay * 1000, Timeout.Infinite);
        }
        if (first) Failed?.Invoke(message);
    }

    private void MarkWorking()
    {
        bool wasFailing;
        lock (_stateLock)
        {
            wasFailing = _failing;
            _failing = false;
            _attempt = 0;
        }
        if (wasFailing) Recovered?.Invoke();
    }

    private void OnData(AudioIngest ingest, WaveFormat format, byte[] buffer, int count)
    {
        try
        {
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
            if (PcmDecoder.TryDecode(buffer.AsSpan(0, count), format.BitsPerSample, isFloat, out var samples))
                ingest.Feed(samples);
            else if (!_loggedBadData)
            {
                _loggedBadData = true;
                AppLog.Warn($"Unsupported audio format: {format}");
                Fail($"This audio device uses a format the app can't read ({format.BitsPerSample}-bit {format.Encoding}).");
            }
        }
        catch (Exception ex)
        {
            // This runs on the device's own thread: an exception here would end the whole process.
            AppLog.Error("Audio processing failed", ex);
        }
    }

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // -- follow the default device -----------------------------------------------------------

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role != Role.Multimedia) return;
        if ((_loopback && flow == DataFlow.Render) || (!_loopback && flow == DataFlow.Capture))
        {
            lock (_stateLock)
            {
                if (_disposed) return;
                _restart?.Dispose();
                _restart = new Timer(_ => Open(), null, 500, Timeout.Infinite); // debounce the burst of change events
            }
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _disposed = true;
            _restart?.Dispose();
        }
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        lock (_lock) Close();
        _enumerator.Dispose();
    }
}
