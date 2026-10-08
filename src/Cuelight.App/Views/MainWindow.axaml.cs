using System.Collections.Specialized;
using Cuelight.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Cuelight.App.Views;

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
            {
                vm.Turns.CollectionChanged += OnTurnsChanged;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(MainViewModel.LiveText) or nameof(MainViewModel.ShowLive))
                        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
                };
            }
        };
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
}
