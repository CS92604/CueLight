using System.Collections.Specialized;
using Assistant.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Assistant.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Platform.CaptureShield.Track(this);
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Turns.CollectionChanged += OnTurnsChanged;
        };
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
}
