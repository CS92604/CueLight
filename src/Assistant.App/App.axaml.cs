using Assistant.App.ViewModels;
using Assistant.App.Views;
using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Assistant.App;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (SelfTest.Active && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime test)
        {
            test.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = SelfTest.RunWindows(test);
            base.OnFrameworkInitializationCompleted();
            return;
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost();
            desktop.Exit += (_, _) => _host.Dispose();
            _host.KeyRemoved += () => ShowWelcome(desktop);

            // An exception on the UI thread (a clipboard that's locked, say) shouldn't close the app.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLog.Error("Unexpected error on the UI thread", e.Exception);
                _host.ReportUnexpected(e.Exception);
                e.Handled = true;
            };

            // Started a second time? Bring this window forward instead.
            if (Program.Single is { } single)
                single.Activated += () => Dispatcher.UIThread.Post(() => BringForward(desktop.MainWindow));

            if (_host.HasKey) ShowMain(desktop);
            else ShowWelcome(desktop);

            // Once the window has been up for a moment, this start counts as having worked.
            DispatcherTimer.RunOnce(Program.Guard.Succeeded, TimeSpan.FromSeconds(3));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void BringForward(Window? window)
    {
        if (window is null) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
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
