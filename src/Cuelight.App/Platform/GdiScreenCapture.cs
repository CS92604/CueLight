using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cuelight.Core;
using Region = Cuelight.Core.Region;

namespace Cuelight.App.Platform;

/// <summary>Screen capture through GDI (Windows). Coordinates are physical pixels.</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapture : IScreenCapture
{
    private const int MaxEdge = 1568; // longer edges are downscaled by the API anyway

    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000; // include layered windows (some chat and video apps draw with them)

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    /// <summary>Copies the pixels of a screen area. This calls the GDI function itself: .NET's own
    /// Graphics.CopyFromScreen rejects the "include layered windows" flag as an invalid argument.</summary>
    private static Bitmap Grab(Region r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var target = g.GetHdc();
            try
            {
                // Fails (access denied) while the screen is locked or a UAC prompt is up; the watcher copes with that.
                if (!BitBlt(target, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, SrcCopy | CaptureBlt))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                g.ReleaseHdc(target);
                ReleaseDC(IntPtr.Zero, screen);
            }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public byte[] CaptureFullPng(Region region)
    {
        using var bmp = Grab(region);
        return ToPng(bmp);
    }

    public byte[] CapturePng(Region region)
    {
        using var bmp = Grab(region);
        int longest = Math.Max(bmp.Width, bmp.Height);
        if (longest <= MaxEdge) return ToPng(bmp);
        double k = MaxEdge / (double)longest;
        using var small = new Bitmap(bmp, new Size(Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k))));
        return ToPng(small);
    }

    public (byte[] Bgra, int Width, int Height) GrabBgra(Region region)
    {
        using var bmp = Grab(region);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return (bytes, bmp.Width, bmp.Height);
        }
        finally { bmp.UnlockBits(data); }
    }
}

/// <summary>Used where screen capture isn't implemented (anything but Windows).</summary>
public sealed class UnsupportedScreenCapture : IScreenCapture
{
    private static Exception Nope() => new PlatformNotSupportedException("Watching the screen is only available on Windows.");
    public byte[] CapturePng(Region region) => throw Nope();
    public byte[] CaptureFullPng(Region region) => throw Nope();
    public (byte[] Bgra, int Width, int Height) GrabBgra(Region region) => throw Nope();
}

public static class PlatformServices
{
    public static IScreenCapture CreateScreenCapture() =>
        OperatingSystem.IsWindows() ? new GdiScreenCapture() : new UnsupportedScreenCapture();

    /// <summary>Windows' own text recognition, or null where there is none (then the text area is sent as a picture).</summary>
    public static ITextReader? CreateTextReader() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ? new WindowsTextReader() : null;
}
