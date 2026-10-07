using Assistant.App.Views;
using Assistant.Core;

namespace Assistant.App.Tests;

public class OutlineGeometryTests
{
    [Fact]
    public void The_four_bars_surround_the_area_without_touching_it()
    {
        var area = new Region(600, 200, 300, 160);
        var bars = RegionOutline.Rects(area);
        Assert.Equal(4, bars.Count);
        foreach (var (x, y, w, h) in bars)
        {
            bool overlaps = x < area.Left + area.Width && x + w > area.Left && y < area.Top + area.Height && y + h > area.Top;
            Assert.False(overlaps, $"the bar at {x},{y} {w}x{h} covers the area");
            Assert.True(w == RegionOutline.Thickness || h == RegionOutline.Thickness, "each bar is thin");
        }
        // Together they make a complete ring: the outer rectangle minus the area.
        long t = RegionOutline.Thickness;
        long outer = (area.Width + 2 * t) * (area.Height + 2 * t);
        Assert.Equal(outer - (long)area.Width * area.Height, bars.Sum(b => (long)b.Width * b.Height));
    }
}
