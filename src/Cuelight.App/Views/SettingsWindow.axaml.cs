using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cuelight.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Platform.CaptureShield.Track(this);
        // Selecting list items scrolls them into view; start at the top regardless.
        Opened += (_, _) =>
        {
            Platform.WindowFit.ClampToScreen(this);
            Platform.WindowFit.KeepOnScreen(this);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Scroller.ScrollToHome(), Avalonia.Threading.DispatcherPriority.Background);
        };
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
