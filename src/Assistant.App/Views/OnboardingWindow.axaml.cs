using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Assistant.App.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
        Platform.CaptureShield.Track(this);
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Opened += (_, _) => KeyBox.Focus();
        KeyBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ViewModels.KeyEntryViewModel vm && vm.SubmitCommand.CanExecute(null))
                vm.SubmitCommand.Execute(null);
        };
    }

    private void OnGetKey(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://console.anthropic.com/settings/keys") { UseShellExecute = true }); }
        catch { /* no default browser registered */ }
    }
}
