using Avalonia.Data.Converters;

namespace Cuelight.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter SelectLabel =
        new FuncValueConverter<bool, string>(hasRegion => hasRegion ? "Reselect" : "Select area");

    public static readonly IValueConverter OnOff =
        new FuncValueConverter<bool, string>(on => on ? "ON" : "OFF");

    public static readonly IValueConverter ContinueLabel =
        new FuncValueConverter<bool, string>(busy => busy ? "Checking your key…" : "Continue");
}
