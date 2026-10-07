namespace Assistant.Core;

/// <summary>A rectangle in physical screen pixels (the coordinate space screen capture works in).</summary>
public readonly record struct Region(int Left, int Top, int Width, int Height)
{
    public override string ToString() => $"{Width} × {Height}";

    /// <summary>
    /// Turn a drag on a selection surface into physical screen pixels.
    /// <paramref name="origin"/> is the physical position of the surface's top-left corner;
    /// <paramref name="scale"/> is physical pixels per surface unit (the display scaling).
    /// Returns null for drags smaller than <paramref name="minSize"/> (stray clicks).
    /// </summary>
    public static Region? FromDrag(
        (int X, int Y) origin, (double X, double Y) scale,
        (double X, double Y) p0, (double X, double Y) p1, int minSize = 16)
    {
        double x0 = Math.Min(p0.X, p1.X), x1 = Math.Max(p0.X, p1.X);
        double y0 = Math.Min(p0.Y, p1.Y), y1 = Math.Max(p0.Y, p1.Y);
        var r = new Region(
            origin.X + (int)Math.Round(x0 * scale.X),
            origin.Y + (int)Math.Round(y0 * scale.Y),
            (int)Math.Round((x1 - x0) * scale.X),
            (int)Math.Round((y1 - y0) * scale.Y));
        return r.Width >= minSize && r.Height >= minSize ? r : null;
    }
}
