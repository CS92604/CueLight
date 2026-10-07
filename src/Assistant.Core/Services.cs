namespace Assistant.Core;

/// <summary>Screen access the engine needs. The Windows implementation lives in the app.</summary>
public interface IScreenCapture
{
    /// <summary>PNG of the region, scaled down if it is larger than the API will read.</summary>
    byte[] CapturePng(Region region);

    /// <summary>Full-resolution PNG of a screen area (used by the area picker).</summary>
    byte[] CaptureFullPng(Region region);

    /// <summary>Raw BGRA pixels of the region (used for change detection).</summary>
    (byte[] Bgra, int Width, int Height) GrabBgra(Region region);
}

/// <summary>Watches one region and reports when it has changed and settled.</summary>
public interface IScreenWatcher : IDisposable
{
    bool Pending { get; }
    void Start();
}

/// <summary>Samples a region on a timer and calls <c>onChange</c> when it has changed and settled.</summary>
public sealed class ScreenWatcher : IScreenWatcher
{
    private readonly Region _region;
    private readonly IScreenCapture _capture;
    private readonly Action _onChange;
    private readonly Action<string> _onError;
    private readonly TimeSpan _interval;
    private readonly ChangeDetector _detector = new();
    private readonly CancellationTokenSource _cts = new();

    public ScreenWatcher(Region region, IScreenCapture capture, Action onChange, Action<string> onError,
        TimeSpan? interval = null)
    {
        _region = region;
        _capture = capture;
        _onChange = onChange;
        _onError = onError;
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    public bool Pending => _detector.Pending;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                var (bgra, w, h) = _capture.GrabBgra(_region);
                if (_detector.Update(GrayFrame.FromBgra(bgra, w, h), clock.Elapsed.TotalSeconds)) _onChange();
            } while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _onError($"Watching the text area stopped: {ex.Message}");
        }
    }

    public void Dispose() => _cts.Cancel();
}
