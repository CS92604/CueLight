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

/// <summary>
/// Samples a region on a timer and calls <c>onChange</c> when it has changed and settled.
///
/// Capturing can fail for a while without anything being wrong with the app: the PC is locked, a
/// permission (UAC) prompt owns the screen, the display is changing, or the PC just woke from
/// sleep. So a failed capture is retried on the next tick. Only after several in a row is it
/// reported once, and when capture works again <c>onRecovered</c> says so. Blank (all black)
/// frames, which a locked screen produces instead of an error, are skipped.
/// </summary>
public sealed class ScreenWatcher : IScreenWatcher
{
    private readonly Region _region;
    private readonly IScreenCapture _capture;
    private readonly Action _onChange;
    private readonly Action<string> _onError;
    private readonly Action? _onRecovered;
    private readonly int _failuresBeforeError;
    private readonly TimeSpan _interval;
    private readonly ChangeDetector _detector = new();
    private readonly CancellationTokenSource _cts = new();

    public ScreenWatcher(Region region, IScreenCapture capture, Action onChange, Action<string> onError,
        TimeSpan? interval = null, Action? onRecovered = null, int failuresBeforeError = 6)
    {
        _region = region;
        _capture = capture;
        _onChange = onChange;
        _onError = onError;
        _onRecovered = onRecovered;
        _failuresBeforeError = Math.Max(1, failuresBeforeError);
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    public bool Pending => _detector.Pending;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int failures = 0;
        bool reported = false;
        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                GrayFrame? frame = null;
                try
                {
                    var (bgra, w, h) = _capture.GrabBgra(_region);
                    frame = GrayFrame.FromBgra(bgra, w, h);
                    if (frame.IsBlack) frame = null; // locked screen or protected content: nothing to compare
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (++failures == _failuresBeforeError)
                    {
                        reported = true;
                        AppLog.Warn($"Screen capture keeps failing: {ex.Message}");
                        _onError($"Can't read the text area right now ({ex.Message}). Still trying.");
                    }
                    continue;
                }

                if (failures > 0)
                {
                    failures = 0;
                    if (reported) { reported = false; _onRecovered?.Invoke(); }
                }
                if (frame is not null && _detector.Update(frame, clock.Elapsed.TotalSeconds)) _onChange();
            } while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Watching the text area stopped", ex);
            _onError($"Watching the text area stopped: {ex.Message}");
        }
    }

    public void Dispose() => _cts.Cancel();
}
