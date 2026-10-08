using Avalonia;
using Avalonia.Controls;

namespace Cuelight.App.Platform;

/// <summary>Keeps windows usable on small screens: a 780-pixel-tall window hangs off the bottom of a
/// 1366×768 laptop, and its buttons can't be reached.</summary>
public static class WindowFit
{
    private const double Margin = 12; // device-independent pixels left free around the window

    /// <summary>Shrink the window (and its minimum size) to the usable area of its screen.</summary>
    public static void ClampToScreen(Window w)
    {
        var screen = w.Screens.ScreenFromVisual(w) ?? w.Screens.Primary;
        if (screen is null) return;
        var (maxW, maxH) = Limits(screen.WorkingArea.Width, screen.WorkingArea.Height, screen.Scaling);
        w.MinWidth = Math.Min(w.MinWidth, maxW);
        w.MinHeight = Math.Min(w.MinHeight, maxH);
        if (w.Width > maxW) w.Width = maxW;
        if (w.Height > maxH) w.Height = maxH;
    }

    /// <summary>Move the window, if needed, so all of it is inside the usable area of its screen.</summary>
    public static void KeepOnScreen(Window w)
    {
        var screen = w.Screens.ScreenFromVisual(w) ?? w.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var size = new PixelSize((int)Math.Ceiling(w.Width * screen.Scaling), (int)Math.Ceiling(w.Height * screen.Scaling));
        int x = Math.Clamp(w.Position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        int y = Math.Clamp(w.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        if (x != w.Position.X || y != w.Position.Y) w.Position = new PixelPoint(x, y);
    }

    /// <summary>The largest window, in device-independent pixels, that fits a working area of the given physical size.</summary>
    public static (double Width, double Height) Limits(int areaWidth, int areaHeight, double scaling) =>
        (Math.Max(300, areaWidth / scaling - 2 * Margin), Math.Max(300, areaHeight / scaling - 2 * Margin));
}
