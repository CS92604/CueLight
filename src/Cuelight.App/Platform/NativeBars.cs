using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuelight.App.Platform;

/// <summary>
/// Solid orange rectangles floating above everything, made directly with Windows. They draw the outline
/// around the watched text area.
///
/// Why not ordinary Avalonia windows: Windows keeps a normal top-level window above a minimum size (about
/// 32 × 38 pixels). A 3-pixel bar made that way came out as a thick slab that grew over the text inside the box.
/// A plain pop-up window made here has no such limit, so each bar is exactly the size it was asked to be.
///
/// The bars are click-through (clicks reach whatever is underneath), never take focus, and stay out of
/// the taskbar and Alt+Tab.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeBars : IDisposable
{
    private const string ClassName = "Cuelight.Outline";
    private const uint Orange = 0x5777D9; // #D97757 as a Windows colour (blue, green, red)

    private static readonly object Gate = new();
    private static bool _registered;
    private static WndProc? _proc; // kept alive as long as the window class exists

    private readonly List<IntPtr> _windows = new();

    /// <summary>The window handles, so they can be registered with <see cref="CaptureShield"/>.</summary>
    public IReadOnlyList<IntPtr> Handles => _windows;

    public NativeBars(IEnumerable<(int X, int Y, int Width, int Height)> rects)
    {
        EnsureClass();
        var instance = GetModuleHandleW(null);
        try
        {
            foreach (var (x, y, w, h) in rects)
            {
                var hwnd = CreateWindowExW(
                    WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
                    ClassName, "", WsPopup, x, y, w, h, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _windows.Add(hwnd);
                // Fully opaque; the layered style is only here so that clicks pass through.
                SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
                SetWindowPos(hwnd, HwndTopmost, x, y, w, h, SwpNoActivate | SwpShowWindow);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Where each bar really is on screen, as Windows reports it (used by the self-test).</summary>
    public IEnumerable<(int X, int Y, int Width, int Height)> ActualBounds()
    {
        foreach (var h in _windows)
            if (GetWindowRect(h, out var r)) yield return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public void Dispose()
    {
        foreach (var h in _windows) DestroyWindow(h);
        _windows.Clear();
    }

    private static void EnsureClass()
    {
        lock (Gate)
        {
            if (_registered) return;
            _proc = (hwnd, msg, wParam, lParam) =>
                msg == WmMouseActivate ? (IntPtr)MaNoActivate : DefWindowProcW(hwnd, msg, wParam, lParam);
            var cls = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                hbrBackground = CreateSolidBrush(Orange),
                lpszClassName = ClassName,
            };
            if (RegisterClassExW(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
    }

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008, WsExTransparent = 0x00000020, WsExToolWindow = 0x00000080,
                      WsExLayered = 0x00080000, WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, LwaAlpha = 0x2;
    private const uint WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
