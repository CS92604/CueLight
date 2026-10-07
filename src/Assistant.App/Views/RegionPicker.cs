using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Region = Assistant.Core.Region;

namespace Assistant.App.Views;

/// <summary>
/// Snipping-tool style area picker: the screen freezes under a dimmed layer and the user drags a
/// box over the text to watch. One full-screen window per monitor, each showing its own pixels.
/// </summary>
public static class RegionPicker
{
    /// <summary>Lets tests reach the picker windows to drive them with simulated input.</summary>
    internal static Action<IReadOnlyList<Window>>? WindowsShown;

    public static async Task<Region?> PickAsync(Window owner, IScreenCapture capture)
    {
        var screens = owner.Screens.All.ToList();
        var shots = new List<(Screen Screen, Bitmap Shot)>();
        owner.Hide();
        try
        {
            await Task.Delay(280); // let our own window disappear before the screenshot
            foreach (var s in screens)
            {
                var png = capture.CaptureFullPng(new Region(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height));
                shots.Add((s, new Bitmap(new MemoryStream(png))));
            }
        }
        catch
        {
            owner.Show();
            owner.Activate();
            throw;
        }

        var done = new TaskCompletionSource<Region?>();
        var windows = shots.Select(x => new PickerWindow(x.Screen, x.Shot, r => done.TrySetResult(r))).ToList();
        try
        {
            foreach (var w in windows) w.Show();
            windows[0].Activate();
            WindowsShown?.Invoke(windows);
            return await done.Task;
        }
        finally
        {
            foreach (var w in windows) w.Close();
            owner.Show();
            owner.Activate();
        }
    }

    private sealed class PickerWindow : Window
    {
        private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0x14, 0x14, 0x13));
        private readonly Screen _screen;
        private readonly Action<Region?> _done;
        private readonly Canvas _canvas = new();
        private readonly Rectangle[] _dim = { new(), new(), new(), new() };
        private readonly Border _box = new()
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#D97757")),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        private readonly Border _sizeTag;
        private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeight.SemiBold };
        private Point? _start;

        public PickerWindow(Screen screen, Bitmap shot, Action<Region?> done)
        {
            _screen = screen;
            _done = done;
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = screen.Bounds.Position;
            Width = screen.Bounds.Width / screen.Scaling;
            Height = screen.Bounds.Height / screen.Scaling;
            Cursor = new Cursor(StandardCursorType.Cross);
            Background = Brushes.Black;

            _canvas.Width = Width;
            _canvas.Height = Height;
            _canvas.Children.Add(new Image { Source = shot, Width = Width, Height = Height, Stretch = Stretch.Fill, IsHitTestVisible = false });
            foreach (var r in _dim) { r.Fill = Dim; r.IsHitTestVisible = false; _canvas.Children.Add(r); }
            _canvas.Children.Add(_box);

            _sizeTag = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E6141413")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3),
                Child = _sizeText,
                IsVisible = false,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_sizeTag);

            var hint = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#EB141413")),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(18, 10),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "Drag over the text you want Claude to watch  ·  Esc to cancel",
                    Foreground = Brushes.White,
                    FontSize = 13,
                },
            };
            hint.Measure(Size.Infinity);
            Canvas.SetLeft(hint, Math.Max(8, (Width - hint.DesiredSize.Width) / 2));
            Canvas.SetTop(hint, 28);
            _canvas.Children.Add(hint);

            Content = _canvas;
            SetSelection(new Rect(0, 0, 0, 0));

            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) _done(null); };
            Platform.CaptureShield.Track(this);
            Opened += (_, _) => Focus();
        }

        private void SetSelection(Rect r)
        {
            void Place(Rectangle rect, double x, double y, double w, double h)
            {
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                rect.Width = Math.Max(0, w); rect.Height = Math.Max(0, h);
            }
            Place(_dim[0], 0, 0, Width, r.Y);                               // above
            Place(_dim[1], 0, r.Bottom, Width, Height - r.Bottom);          // below
            Place(_dim[2], 0, r.Y, r.X, r.Height);                          // left
            Place(_dim[3], r.Right, r.Y, Width - r.Right, r.Height);        // right
            if (r.Width == 0 && r.Height == 0) { Place(_dim[0], 0, 0, Width, Height); _box.IsVisible = false; _sizeTag.IsVisible = false; return; }
            Canvas.SetLeft(_box, r.X - 2); Canvas.SetTop(_box, r.Y - 2);
            _box.Width = r.Width + 4; _box.Height = r.Height + 4;
            _box.IsVisible = true;
            _sizeText.Text = $"{Math.Round(r.Width * RenderScaling)} × {Math.Round(r.Height * RenderScaling)}";
            Canvas.SetLeft(_sizeTag, r.X);
            Canvas.SetTop(_sizeTag, r.Bottom + 8 + 26 < Height ? r.Bottom + 8 : Math.Max(4, r.Y - 34));
            _sizeTag.IsVisible = true;
        }

        private static Rect Normalize(Point a, Point b) =>
            new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private void OnPressed(object? s, PointerPressedEventArgs e)
        {
            var p = e.GetCurrentPoint(this);
            if (p.Properties.IsRightButtonPressed) { _done(null); return; }
            if (p.Properties.IsLeftButtonPressed) { _start = p.Position; e.Pointer.Capture(this); }
        }

        private void OnMoved(object? s, PointerEventArgs e)
        {
            if (_start is { } a) SetSelection(Normalize(a, e.GetPosition(this)));
        }

        private void OnReleased(object? s, PointerReleasedEventArgs e)
        {
            if (_start is not { } a) return;
            e.Pointer.Capture(null);
            var b = e.GetPosition(this);
            var region = Region.FromDrag(
                (_screen.Bounds.X, _screen.Bounds.Y), (RenderScaling, RenderScaling), (a.X, a.Y), (b.X, b.Y));
            _start = null;
            if (region is null) { SetSelection(new Rect(0, 0, 0, 0)); return; } // a stray click: let them retry
            _done(region);
        }
    }
}

/// <summary>Four thin bars just outside the watched box, so it stays visible without ever
/// covering (or being captured with) the text inside it.</summary>
public sealed class RegionOutline : IDisposable
{
    private const int Thickness = 3; // physical pixels
    private readonly List<Window> _bars = new();

    public void Show(Window owner, Region r)
    {
        Hide();
        var screen = owner.Screens.ScreenFromPoint(new PixelPoint(r.Left + r.Width / 2, r.Top + r.Height / 2)) ?? owner.Screens.Primary;
        double scale = screen?.Scaling ?? 1.0;
        int t = Thickness;
        foreach (var (x, y, w, h) in new[]
        {
            (r.Left - t, r.Top - t, r.Width + 2 * t, t),   // top
            (r.Left - t, r.Top + r.Height, r.Width + 2 * t, t), // bottom
            (r.Left - t, r.Top, t, r.Height),               // left
            (r.Left + r.Width, r.Top, t, r.Height),         // right
        })
        {
            var bar = new Window
            {
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                CanResize = false,
                Focusable = false,
                Background = new SolidColorBrush(Color.Parse("#D97757")),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(x, y),
                Width = w / scale,
                Height = h / scale,
                MinWidth = 0,
                MinHeight = 0,
            };
            Platform.CaptureShield.Track(bar);
            bar.Show();
            _bars.Add(bar);
        }
    }

    public void Hide()
    {
        foreach (var b in _bars) b.Close();
        _bars.Clear();
    }

    public void Dispose() => Hide();
}
