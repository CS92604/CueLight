using Assistant.App.ViewModels;
using Assistant.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Assistant.App;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost();
            desktop.Exit += (_, _) => _host.Dispose();
            _host.KeyRemoved += () => ShowWelcome(desktop);
            if (_host.HasKey) ShowMain(desktop);
            else ShowWelcome(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowMain(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var main = _host!.CreateMainWindow();
        var previous = desktop.MainWindow;
        desktop.MainWindow = main;
        main.Show();
        previous?.Close();
    }

    private void ShowWelcome(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var entry = new KeyEntryViewModel();
        var welcome = new OnboardingWindow { DataContext = entry };
        var previous = desktop.MainWindow;
        entry.Accepted = key =>
        {
            _host!.SetKey(key);
            ShowMain(desktop); // closes this window once the assistant is up
        };
        desktop.MainWindow = welcome;
        welcome.Show();
        previous?.Close();
    }
}
