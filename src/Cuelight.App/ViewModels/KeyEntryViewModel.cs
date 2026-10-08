using Cuelight.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cuelight.App.ViewModels;

/// <summary>Choose an AI provider, enter its API key, check it with that provider, and hand it back. Used by the
/// welcome screen and by Settings.</summary>
public sealed partial class KeyEntryViewModel : ObservableObject
{
    private readonly Func<string, Task<(bool Ok, string Message)>>? _validate;

    /// <param name="validate">Checks a key. Without one, the key is checked with the chosen provider.</param>
    public KeyEntryViewModel(Func<string, Task<(bool Ok, string Message)>>? validate = null) => _validate = validate;

    /// <summary>The providers as the segmented control lists them, in <see cref="Provider"/> order.</summary>
    public static IReadOnlyList<ChoiceItem> ProviderItems { get; } =
        Providers.All.Select(p => new ChoiceItem(p.Name, p.Blurb)).ToArray();

    /// <summary>Called with the trimmed key once the provider has accepted it.</summary>
    public Action<string>? Accepted { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _key = "";

    [ObservableProperty] private string _error = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PasswordChar))] private bool _isRevealed;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private Provider _provider = Provider.Claude;
    /// <summary>The address of an OpenAI-style service (provider Other).</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private string _baseUrl = "";
    /// <summary>The model to use with that service (provider Other).</summary>
    [ObservableProperty] private string _modelId = "";

    public bool HasError => Error.Length > 0;
    public char PasswordChar => IsRevealed ? '\0' : '•';

    public ProviderInfo Info => Providers.Get(Provider);
    public int ProviderIndex
    {
        get => (int)Provider;
        set { if (value >= 0 && value < ProviderItems.Count) Provider = (Provider)value; }
    }

    public string KeyLabel => Info.IsCustom ? "API key (optional)" : $"{Info.Name} API key";
    public string KeyHint => Info.KeyHint;
    public bool ShowAddress => Info.IsCustom;
    public bool HasKeyPage => Info.HasKeyPage;
    public string KeyTip => Info.IsCustom
        ? "The key for the service, if it needs one; leave it empty for a service that doesn't (a model running on this PC, say). It is stored encrypted on this PC and only ever sent to that service."
        : $"Your {Info.Name} API key, from {Info.KeyHost}. It is stored encrypted on this PC and only ever sent to {Info.Company}.";
    public string GetKeyLabel => $"Get a key at {Info.KeyHost}";
    public string GetKeyTip => $"Opens {Info.KeyHost} in your browser, where you can create a key.";
    public string ContinueTip => Info.IsCustom ? "Check that the service answers, then start." : $"Check the key with {Info.Company} and start.";

    public string PrivacyText => Info.IsCustom
        ? "Your key is stored encrypted on this PC and is only ever sent to the service you enter. Speech is turned into text on your PC; only text, and (if you pick a text area) either its words or a picture of it, is sent to that service."
        : $"Your key is stored encrypted on this PC and is only ever sent to {Info.Company}. Speech is turned into text on your PC; only text, and (if you pick a text area) either its words or a picture of it, is sent to {Info.Name}.";

    public string BillingText => Provider switch
    {
        Provider.Claude => "Anthropic bills API use separately from a Claude subscription. A suggestion costs a fraction of a dollar; the app shows the total.",
        Provider.OpenAi => "OpenAI bills API use separately from a ChatGPT subscription. A suggestion costs a fraction of a dollar; the app shows an estimate.",
        Provider.Gemini => "Google bills Gemini API use through its own account, separate from any Gemini subscription. A suggestion costs a fraction of a dollar; the app shows an estimate.",
        Provider.Grok => "xAI bills API use separately from a Grok subscription. A suggestion costs a fraction of a dollar; the app shows an estimate.",
        Provider.Nvidia => "NVIDIA's API catalog is free to try, within limits it sets (about 40 requests a minute is typical). It is meant for testing, and a free service may keep what you send it, so read NVIDIA's terms before relying on it.",
        _ => "The service bills you for its use, however it charges. The app shows an estimate when it knows the model's price, and the number of tokens when it doesn't.",
    };

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnProviderChanged(Provider value)
    {
        Error = "";
        foreach (var name in new[]
        {
            nameof(ProviderIndex), nameof(Info), nameof(KeyLabel), nameof(KeyHint), nameof(KeyTip), nameof(ShowAddress), nameof(HasKeyPage),
            nameof(GetKeyLabel), nameof(GetKeyTip), nameof(ContinueTip), nameof(PrivacyText), nameof(BillingText),
        })
            OnPropertyChanged(name);
    }

    public void Reset()
    {
        Key = "";
        Error = "";
        IsBusy = false;
        IsRevealed = false;
    }

    // Another service may need no key at all (a model on this PC), so there only the address is required to go on.
    private bool CanSubmit() => !IsBusy && (Key.Trim().Length > 0 || (Provider == Provider.Other && BaseUrl.Trim().Length > 0));

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        var key = Key.Trim();
        Error = "";
        if (Provider == Provider.Claude)
        {
            if (!key.StartsWith("sk-ant-", StringComparison.Ordinal))
            {
                Error = "That doesn't look like a Claude API key. Keys start with “sk-ant-”.";
                return;
            }
        }
        else if (Provider == Provider.Other)
        {
            if (BaseUrl.Trim().Length == 0)
            {
                Error = "Enter the service's address first, for example https://openrouter.ai/api/v1";
                return;
            }
            if (ModelId.Trim().Length == 0)
            {
                Error = "Enter the name of the model to use, for example llama3.2.";
                return;
            }
            if (key.Length == 0) key = ProviderKeys.NoKey;
        }
        else if (Providers.Detect(key) is { } other && other != Provider)
        {
            var name = Providers.Get(other).Name;
            Error = $"That looks like a {name} key. Choose {name} above, or paste a {Info.Name} key.";
            return;
        }

        IsBusy = true;
        try
        {
            var (ok, message) = await (_validate?.Invoke(key) ?? KeyValidator.ValidateAsync(Provider, key, BaseUrl.Trim()));
            if (!ok) { Error = message; return; }
            Accepted?.Invoke(key);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void ToggleReveal() => IsRevealed = !IsRevealed;
}
