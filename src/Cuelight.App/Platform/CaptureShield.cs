using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Cuelight.App.Platform;

/// <summary>
/// Keeps the app's own windows out of screen captures, screen shares and recordings, using
/// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) (Windows 10 version 2004 or later).
///
/// This is what the capture APIs behind Teams, Zoom, Meet, OBS, the Snipping Tool and the Xbox
/// Game Bar respect. It does not hide anything from a camera pointed at the screen, a capture
/// card, or software that reads the display some other way. Every window created by the app is
/// registered with <see cref="Track"/>, so the setting covers the picker and the outline too.
/// </summary>
public static class CaptureShield
{
    private const uint WdaNone = 0x00;
    private const uint WdaExcludeFromCapture = 0x11;

    private static readonly List<WeakReference<Window>> Windows = new();
    private static readonly List<IntPtr> Handles = new(); // windows made directly with Windows, not by Avalonia
    private static bool _hidden;

    /// <summary>Test seams: how a window is actually flagged, and whether this OS can do it.</summary>
    internal static Func<Window, bool, bool> Apply { get; set; } = ApplyToWindow;
    internal static Func<IntPtr, bool, bool> ApplyHandle { get; set; } = ApplyToHandle;
    internal static Func<bool> SupportCheck { get; set; } =
        () => OperatingSystem.IsWindows() && Environment.OSVersion.Version >= new Version(10, 0, 19041);

    public static bool IsSupported => SupportCheck();
    public static bool IsHidden => _hidden;

    /// <summary>Register a window so it follows the setting, now and whenever it changes.</summary>
    public static void Track(Window window)
    {
        lock (Windows) Windows.Add(new WeakReference<Window>(window));
        window.Opened += (_, _) => { if (_hidden) Apply(window, true); };
        window.Closed += (_, _) =>
        {
            lock (Windows) Windows.RemoveAll(r => !r.TryGetTarget(out var w) || ReferenceEquals(w, window));
        };
    }

    /// <summary>Register a window made directly with Windows (see <see cref="NativeBars"/>).</summary>
    public static void TrackHandle(IntPtr handle)
    {
        lock (Windows) Handles.Add(handle);
        if (_hidden) ApplyHandle(handle, true);
    }

    public static void UntrackHandle(IntPtr handle)
    {
        lock (Windows) Handles.Remove(handle);
    }

    /// <summary>Hide or show every tracked window. Returns false if Windows refused (or can't do it here).</summary>
    public static bool SetHidden(bool hidden)
    {
        if (hidden && !IsSupported) return false;
        _hidden = hidden;
        List<Window> open;
        lock (Windows) open = Windows.Select(r => r.TryGetTarget(out var w) ? w : null).OfType<Window>().ToList();
        List<IntPtr> handles;
        lock (Windows) handles = Handles.ToList();
        bool ok = true;
        foreach (var w in open) ok &= Apply(w, hidden);
        foreach (var h in handles) ok &= ApplyHandle(h, hidden);
        return ok;
    }

    private static bool ApplyToWindow(Window window, bool hidden)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return true; // not created yet: it is flagged when it opens
        return SetWindowDisplayAffinity(handle, hidden ? WdaExcludeFromCapture : WdaNone);
    }

    private static bool ApplyToHandle(IntPtr handle, bool hidden) =>
        OperatingSystem.IsWindows() && handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, hidden ? WdaExcludeFromCapture : WdaNone);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
