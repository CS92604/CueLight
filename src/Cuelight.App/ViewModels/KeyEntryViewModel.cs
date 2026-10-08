using Cuelight.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cuelight.App.ViewModels;

/// <summary>Enter a Claude API key, check it with Anthropic, and hand it back. Used by the welcome
/// screen and by Settings.</summary>
public sealed partial class KeyEntryViewModel : ObservableObject
{
    private readonly Func<string, Task<(bool Ok, string Message)>> _validate;

    public KeyEntryViewModel(Func<string, Task<(bool Ok, string Message)>>? validate = null) =>
        _validate = validate ?? (key => ClaudeSuggester.ValidateKeyAsync(key));

    /// <summary>Called with the trimmed key once Anthropic has accepted it.</summary>
    public Action<string>? Accepted { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _key = "";

    [ObservableProperty] private string _error = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PasswordChar))] private bool _isRevealed;

    public bool HasError => Error.Length > 0;
    public char PasswordChar => IsRevealed ? '\0' : '•';

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public void Reset()
    {
        Key = "";
        Error = "";
        IsBusy = false;
        IsRevealed = false;
    }

    private bool CanSubmit() => !IsBusy && Key.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        var key = Key.Trim();
        Error = "";
        if (!key.StartsWith("sk-ant-", StringComparison.Ordinal))
        {
            Error = "That doesn't look like a Claude API key. Keys start with “sk-ant-”.";
            return;
        }
        IsBusy = true;
        try
        {
            var (ok, message) = await _validate(key);
            if (!ok) { Error = message; return; }
            Accepted?.Invoke(key);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void ToggleReveal() => IsRevealed = !IsRevealed;
}
