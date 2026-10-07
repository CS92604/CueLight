using Assistant.Core;

namespace Assistant.Tests;

public class ChangeDetectorTests
{
    // A 60x160 "screen" with some dark blocks; `blocks` adds more.
    static GrayFrame Frame(int blocks = 0)
    {
        var d = new float[60 * 160];
        Array.Fill(d, 240f);
        for (int i = 0; i < blocks; i++)
            for (int y = 10 + 8 * i; y < 14 + 8 * i && y < 60; y++)
                for (int x = 10; x < 100; x++) d[y * 160 + x] = 20;
        return new GrayFrame(160, 60, d);
    }

    // A block that jumps somewhere new every sample (like video).
    static GrayFrame Moving(int i)
    {
        var d = new float[60 * 160];
        Array.Fill(d, 240f);
        int row = 2 + (i * 7) % 45;
        for (int y = row; y < row + 6; y++) for (int x = 20; x < 140; x++) d[y * 160 + x] = 20;
        return new GrayFrame(160, 60, d);
    }

    static List<double> Run(ChangeDetector det, IEnumerable<(double T, GrayFrame F)> frames)
        => frames.Where(f => det.Update(f.F, f.T)).Select(f => f.T).ToList();

    [Fact]
    public void No_change_never_fires() =>
        Assert.Empty(Run(new ChangeDetector(), Enumerable.Range(0, 40).Select(i => (i * 0.5, Frame(2)))));

    [Fact]
    public void Fires_once_after_the_change_settles()
    {
        var det = new ChangeDetector(settleS: 1.0);
        var fired = Run(det, new[] { (0.0, Frame(1)), (0.5, Frame(1)), (1.0, Frame(2)), (1.5, Frame(2)), (2.0, Frame(2)), (2.5, Frame(2)), (3.0, Frame(2)) });
        Assert.Equal(new[] { 2.0 }, fired); // appeared at 1.0, stayed still for 1s after it
    }

    [Fact]
    public void Does_not_fire_while_still_changing()
    {
        var det = new ChangeDetector(settleS: 1.0, maxWaitS: 100);
        var seq = new List<(double, GrayFrame)> { (0.0, Frame(1)) };
        for (int i = 1; i < 6; i++) seq.Add((0.5 * i, Frame(1 + i)));
        Assert.Empty(Run(det, seq));
        Assert.NotEmpty(Run(det, Enumerable.Range(0, 5).Select(i => (3.0 + 0.5 * i, Frame(6)))));
    }

    [Fact]
    public void Blinking_cursor_is_ignored()
    {
        var baseFrame = Frame(2);
        var blink = new GrayFrame(160, 60, (float[])baseFrame.Data.Clone());
        for (int y = 40; y < 48; y++) for (int x = 105; x < 107; x++) blink.Data[y * 160 + x] = 20;
        Assert.Empty(Run(new ChangeDetector(), Enumerable.Range(0, 40).Select(i => (i * 0.5, i % 2 == 1 ? blink : baseFrame))));
    }

    [Fact]
    public void Returning_to_baseline_cancels_a_pending_change()
    {
        var det = new ChangeDetector(settleS: 1.0);
        Assert.Empty(Run(det, new[] { (0.0, Frame(1)), (0.5, Frame(3)), (1.0, Frame(1)), (1.5, Frame(1)), (3.0, Frame(1)) }));
    }

    [Fact]
    public void Continuous_motion_fires_at_max_wait_then_rate_limits()
    {
        var det = new ChangeDetector(settleS: 1.0, maxWaitS: 4.0, minIntervalS: 3.0);
        var fired = Run(det, Enumerable.Range(0, 40).Select(i => (0.5 * i, Moving(i))));
        Assert.InRange(fired.Count, 2, 6);
        for (int i = 1; i < fired.Count; i++) Assert.True(fired[i] - fired[i - 1] >= 3.0);
    }

    [Fact]
    public void Pending_is_true_only_between_change_and_report()
    {
        var det = new ChangeDetector(settleS: 1.0);
        det.Update(Frame(1), 0.0);
        Assert.False(det.Pending);
        det.Update(Frame(3), 0.5);
        Assert.True(det.Pending);
        det.Update(Frame(3), 1.0);
        Assert.True(det.Pending);
        Assert.True(det.Update(Frame(3), 1.5));
        Assert.False(det.Pending);
    }

    [Fact]
    public void Downsample_keeps_thin_dark_text_visible()
    {
        const int w = 800, h = 300;
        var bgra = new byte[w * h * 4];
        Array.Fill(bgra, (byte)255);
        for (int y = 100; y < 102; y++) for (int x = 100; x < 500; x++) { int i = (y * w + x) * 4; bgra[i] = bgra[i + 1] = bgra[i + 2] = 0; }
        var small = GrayFrame.FromBgra(bgra, w, h);
        Assert.True(small.Width <= 160);
        Assert.True(small.Data.Min() < 200);
    }

    [Fact]
    public void Drag_maps_surface_units_to_physical_pixels_in_any_direction()
    {
        var r = Region.FromDrag((100, 50), (2.0, 2.0), (30, 40), (10, 10));
        Assert.Equal(new Region(120, 70, 40, 60), r);
    }

    [Fact]
    public void Drag_origin_can_be_negative_for_left_hand_monitors()
        => Assert.Equal(new Region(-1820, 100, 300, 200), Region.FromDrag((-1920, 0), (1, 1), (100, 100), (400, 300)));

    [Fact]
    public void Tiny_drag_is_rejected() => Assert.Null(Region.FromDrag((0, 0), (1, 1), (5, 5), (10, 9)));
}
