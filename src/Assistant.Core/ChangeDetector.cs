namespace Assistant.Core;

/// <summary>A small grayscale picture of the watched area, used only for change detection.</summary>
public sealed record GrayFrame(int Width, int Height, float[] Data)
{
    /// <summary>Every pixel is (near) black, as when the screen is locked or the content is protected.</summary>
    public bool IsBlack => Data.All(v => v < 1f);

    /// <summary>Area-averaged downsample of a BGRA bitmap so thin text strokes survive.</summary>
    public static GrayFrame FromBgra(ReadOnlySpan<byte> bgra, int width, int height, int targetWidth = 160)
    {
        int step = Math.Max(1, width / targetWidth);
        int w = width / step, h = height / step;
        if (w == 0 || h == 0) return new GrayFrame(1, 1, new float[1]);
        var data = new float[w * h];
        float norm = 1f / (step * step);
        for (int by = 0; by < h; by++)
        {
            for (int bx = 0; bx < w; bx++)
            {
                float sum = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int row = ((by * step + dy) * width + bx * step) * 4;
                    for (int dx = 0; dx < step; dx++)
                    {
                        int i = row + dx * 4;
                        sum += bgra[i + 2] * 0.299f + bgra[i + 1] * 0.587f + bgra[i] * 0.114f;
                    }
                }
                data[by * w + bx] = sum * norm;
            }
        }
        return new GrayFrame(w, h, data);
    }
}

/// <summary>
/// Decides when the watched region has meaningfully changed and then settled. Feed it a frame
/// every sample; <see cref="Update"/> returns true once per change, after the content has stopped
/// moving for <c>settleS</c> (so a message still being typed isn't read half-finished). Blinking
/// cursors and similarly tiny differences are ignored; continuous change (video) fires at most
/// every <c>minIntervalS</c>.
/// </summary>
public sealed class ChangeDetector
{
    private readonly float _pixelDelta;
    private readonly double _changedFraction, _settleS, _maxWaitS, _minIntervalS;
    private GrayFrame? _baseline, _prev;
    private double? _firstChange;
    private double _lastMotion;
    private double _lastFire = -1e9;

    public ChangeDetector(float pixelDelta = 24f, double changedFraction = 0.002,
        double settleS = 1.0, double maxWaitS = 8.0, double minIntervalS = 3.0)
    {
        _pixelDelta = pixelDelta;
        _changedFraction = changedFraction;
        _settleS = settleS;
        _maxWaitS = maxWaitS;
        _minIntervalS = minIntervalS;
    }

    /// <summary>True while the region differs from the last reported state but hasn't settled.</summary>
    public bool Pending => _firstChange.HasValue;

    public void Reset()
    {
        _baseline = _prev = null;
        _firstChange = null;
    }

    private bool Differs(GrayFrame a, GrayFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return true;
        int changed = 0;
        for (int i = 0; i < a.Data.Length; i++)
            if (Math.Abs(a.Data[i] - b.Data[i]) > _pixelDelta) changed++;
        return changed / (double)a.Data.Length > _changedFraction;
    }

    public bool Update(GrayFrame frame, double now)
    {
        if (_baseline is null || _baseline.Width != frame.Width || _baseline.Height != frame.Height)
        {
            _baseline = _prev = frame;
            _firstChange = null;
            return false;
        }

        bool moved = Differs(frame, _prev!);
        _prev = frame;
        if (!Differs(frame, _baseline))
        {
            _firstChange = null; // back to what we last reported (or never really changed)
            return false;
        }

        if (_firstChange is null)
        {
            _firstChange = now;
            _lastMotion = now;
        }
        else if (moved)
        {
            _lastMotion = now;
        }

        bool settled = now - _lastMotion >= _settleS;
        bool tooLong = now - _firstChange.Value >= _maxWaitS;
        if ((settled || tooLong) && now - _lastFire >= _minIntervalS)
        {
            _baseline = frame;
            _firstChange = null;
            _lastFire = now;
            return true;
        }
        return false;
    }
}
