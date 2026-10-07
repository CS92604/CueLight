using System.Globalization;
using Avalonia.Data.Converters;

namespace Assistant.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter SelectLabel =
        new FuncValueConverter<bool, string>(hasRegion => hasRegion ? "Reselect" : "Select");

    public static readonly IValueConverter ContinueLabel =
        new FuncValueConverter<bool, string>(busy => busy ? "Checking your key…" : "Continue");
}
