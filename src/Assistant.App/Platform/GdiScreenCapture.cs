using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Assistant.Core;
using Region = Assistant.Core.Region;

namespace Assistant.App.Platform;

/// <summary>Screen capture through GDI (Windows). Coordinates are physical pixels.</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapture : IScreenCapture
{
    private const int MaxEdge = 1568; // longer edges are downscaled by the API anyway
    private const CopyPixelOperation CaptureBlt = (CopyPixelOperation)0x40000000; // include layered windows

    private static Bitmap Grab(Region r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy | CaptureBlt);
        return bmp;
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
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
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
}
