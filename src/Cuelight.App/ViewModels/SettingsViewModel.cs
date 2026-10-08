using Cuelight.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cuelight.App.ViewModels;

public sealed record SpeechChoice(SpeechAccuracy Value, string Name, string Blurb, string Tip = "");

public sealed record ScreenChoice(ScreenReading Value, string Name, string Blurb, string Tip = "");

/// <summary>One option of a segmented control, with the explanation shown when the mouse rests on it.</summary>
public sealed record ChoiceItem(string Label, string Tip)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Settings _s;
    private readonly Action _changed;

    /// <param name="currentKey">The saved key of the current provider.</param>
    /// <param name="keyChanged">Called with a new key for the current provider, or null when it is removed.</param>
    /// <param name="keyFor">The saved key of any provider, for when the provider is switched here.</param>
    /// <param name="textReadingAvailable">Whether this PC can read the words in a screen area (Fast screen reading); assumed so if not given.</param>
    public SettingsViewModel(Settings settings, Action changed, KeyEntryViewModel keyEntry, string? currentKey, Action<string?> keyChanged,
        Func<Provider, string?>? keyFor = null, Func<bool>? textReadingAvailable = null)
    {
        _s = settings;
        _changed = changed;
        _textReadingAvailable = textReadingAvailable ?? (() => true);
        KeyEntry = keyEntry;
        _keyChanged = keyChanged;
        var startedWith = settings.Provider;
        _keyFor = keyFor ?? (p => p == startedWith ? currentKey : null);
        _maskedKey = currentKey is null ? NoKeyText : ApiKeyStore.Mask(currentKey);
        keyEntry.Provider = settings.Provider;
        keyEntry.BaseUrl = settings.BaseUrl;
        keyEntry.ModelId = settings.Model;
        keyEntry.Accepted = key =>
        {
            MaskedKey = key == ProviderKeys.NoKey ? "No key needed" : ApiKeyStore.Mask(key);
            IsEditingKey = false;
            _keyChanged(key);
        };
    }

    private readonly Func<bool> _textReadingAvailable;
    private readonly Action<string?> _keyChanged;
    private readonly Func<Provider, string?> _keyFor;
    private const string NoKeyText = "No key saved";

    public static IReadOnlyList<ChoiceItem> ProfessionalismItems { get; } = new ChoiceItem[]
    {
        new("Very casual", "Like texting a friend: contractions, relaxed phrasing, light slang is fine."),
        new("Casual", "Friendly, like talking to a colleague you get along with."),
        new("Professional", "Polite and clear, no slang, but still natural rather than stiff."),
        new("Formal", "Courteous and precise, for a senior person or a formal setting."),
    };

    public static IReadOnlyList<ChoiceItem> ProficiencyItems { get; } =
        Enum.GetValues<Proficiency>().Select(p => new ChoiceItem(p.ToString(), Settings.Caption(p))).ToArray();

    public static IReadOnlyList<ChoiceItem> ToneItems { get; } = new ChoiceItem[]
    {
        new("Warm", "Personable and friendly."),
        new("Neutral", "Matter-of-fact, with no extra warmth."),
        new("Direct", "To the point, without padding."),
        new("Diplomatic", "Tactful: softens anything that could land badly."),
        new("Confident", "Assertive without being aggressive."),
    };

    public static IReadOnlyList<ChoiceItem> LengthItems { get; } = new ChoiceItem[]
    {
        new("Brief", "One short sentence per option."),
        new("Short", "One or two sentences per option."),
        new("Detailed", "Two to four sentences, with a reason or an example where it helps."),
    };

    public static IReadOnlyList<ChoiceItem> OptionItems { get; } = new ChoiceItem[]
    {
        new("1", "One suggestion for each of SAY and TYPE."),
        new("2", "Two suggestions to choose from."),
        new("3", "Three suggestions to choose from."),
    };

    public static IReadOnlyList<SpeechChoice> SpeechItems { get; } = new[]
    {
        new SpeechChoice(SpeechAccuracy.Fast, "Fast", "75 MB download, lowest accuracy",
            "The smallest speech model. Keeps up on slower PCs, but makes more mistakes."),
        new SpeechChoice(SpeechAccuracy.Balanced, "Balanced", "140 MB download, good for most calls",
            "Good accuracy for most calls on a typical PC."),
        new SpeechChoice(SpeechAccuracy.Accurate, "Accurate", "470 MB download, best accuracy, slower",
            "The most accurate. Needs a fast PC, and may fall behind live speech on a slow one."),
    };

    public static IReadOnlyList<ScreenChoice> ScreenItems { get; } = new[]
    {
        new ScreenChoice(ScreenReading.Fast, "Fast", "Reads the words on this PC and sends only text. Quicker and cheaper.",
            "Fast: the text area is read on this PC with Windows' own text recognition, and only the words are sent to the AI, not a picture. It costs less, "
            + "starts a little quicker, and is the only way a model that can't read pictures can reply to the text area. The AI can't see layout, colours or "
            + "images, so it works out who wrote what from names and wording, and a misread word can slip in."),
        new ScreenChoice(ScreenReading.Detailed, "Detailed", "Sends a picture of the area each time it changes. Sees everything.",
            "Detailed: a picture of the text area is sent to the AI each time it changes, so it sees exactly what you see: layout, colours, who wrote each "
            + "message, images. It costs more, and needs a model that can read pictures."),
    };

    public ScreenChoice? SelectedScreen
    {
        get => ScreenItems.FirstOrDefault(s => s.Value == _s.ScreenReading);
        set
        {
            if (value is null) return;
            _s.ScreenReading = value.Value;
            OnPropertyChanged(nameof(TextReadingMissing));
            Changed(nameof(SelectedScreen));
        }
    }

    /// <summary>Fast is chosen but this PC can't read text from the screen, so a picture is sent instead.</summary>
    public bool TextReadingMissing => _s.ScreenReading == ScreenReading.Fast && !_textReadingAvailable();

    public KeyEntryViewModel KeyEntry { get; }

    private void Changed(string name)
    {
        OnPropertyChanged(name);
        _changed();
    }

    public int ProfessionalismIndex { get => (int)_s.Professionalism; set { if (value < 0) return; _s.Professionalism = (Professionalism)value; Changed(nameof(ProfessionalismIndex)); } }
    public int ProficiencyIndex
    {
        get => (int)_s.Proficiency;
        set
        {
            if (value < 0) return;
            _s.Proficiency = (Proficiency)value;
            OnPropertyChanged(nameof(ProficiencyCaption));
            Changed(nameof(ProficiencyIndex));
        }
    }
    public string ProficiencyCaption => Settings.Caption(_s.Proficiency);
    public int ToneIndex { get => (int)_s.Tone; set { if (value < 0) return; _s.Tone = (Tone)value; Changed(nameof(ToneIndex)); } }
    public int LengthIndex { get => (int)_s.Length; set { if (value < 0) return; _s.Length = (ReplyLength)value; Changed(nameof(LengthIndex)); } }
    public int OptionsIndex { get => _s.Options - 1; set { if (value < 0) return; _s.Options = value + 1; Changed(nameof(OptionsIndex)); } }
    public string ReplyLanguage { get => _s.ReplyLanguage; set { _s.ReplyLanguage = value ?? ""; Changed(nameof(ReplyLanguage)); } }
    public string CustomInstructions { get => _s.CustomInstructions; set { _s.CustomInstructions = value ?? ""; Changed(nameof(CustomInstructions)); } }
    public bool ThinkFirst { get => _s.ThinkFirst; set { _s.ThinkFirst = value; Changed(nameof(ThinkFirst)); } }
    public bool UseMicrophone { get => _s.UseMicrophone; set { _s.UseMicrophone = value; Changed(nameof(UseMicrophone)); } }

    /// <summary>Whether this PC can keep windows out of screen capture (Windows 10 version 2004+).</summary>
    public bool HideSupported => Platform.CaptureShield.IsSupported;
    public string HideTip => HideSupported
        ? "Hide from screen sharing. On: this app's windows are left out of screen shares, recordings and screenshots, in Teams, Zoom, Meet, OBS and the Snipping Tool. A camera pointed at your screen can still see them."
        : "Hide from screen sharing isn't available on this PC. It needs Windows 10 version 2004 or later.";

    public bool HideFromCapture
    {
        get => _s.HideFromCapture && HideSupported;
        set { _s.HideFromCapture = value && HideSupported; Changed(nameof(HideFromCapture)); }
    }

    // -- provider and model -------------------------------------------------------------------

    public static IReadOnlyList<ChoiceItem> ProviderItems => KeyEntryViewModel.ProviderItems;

    private ProviderInfo Info => Providers.Get(_s.Provider);

    public int ProviderIndex
    {
        get => (int)_s.Provider;
        set
        {
            if (value < 0 || value >= ProviderItems.Count || (Provider)value == _s.Provider) return;
            SwitchProvider((Provider)value);
        }
    }

    private void SwitchProvider(Provider provider)
    {
        _s.UseProvider(provider);
        KeyEntry.Provider = provider;
        KeyEntry.Reset();
        KeyEntry.BaseUrl = _s.BaseUrl;
        KeyEntry.ModelId = _s.Model;
        var key = _keyFor(provider);
        MaskedKey = key is null ? NoKeyText : key == ProviderKeys.NoKey ? "No key needed" : ApiKeyStore.Mask(key);
        IsEditingKey = key is null;   // nothing saved for this provider yet: ask for it right away
        foreach (var name in new[]
        {
            nameof(ProviderIndex), nameof(ProviderBlurb), nameof(KeyEyebrow), nameof(KeyTip), nameof(CheckSaveTip), nameof(KeyHint),
            nameof(ModelEyebrow), nameof(ModelItems), nameof(SelectedModel), nameof(ShowModelList), nameof(ModelId), nameof(ShowModelId),
            nameof(ShowAddress), nameof(BaseUrl), nameof(ThinkTip), nameof(ThinkBlurb),
        })
            OnPropertyChanged(name);
        _changed();
    }

    public string ProviderBlurb => Info.Blurb;
    public string KeyEyebrow => Info.IsCustom ? "API KEY (OPTIONAL)" : $"{Info.Name.ToUpperInvariant()} API KEY";
    public string KeyHint => Info.KeyHint;
    public string KeyTip => Info.IsCustom
        ? "The key for the service, if it needs one. It is stored encrypted on this PC and only ever sent to that service."
        : $"Your {Info.Name} API key, from {Info.KeyHost}. It is stored encrypted on this PC and only ever sent to {Info.Company}.";
    public string CheckSaveTip => Info.IsCustom ? "Check that the service answers, then save the key, encrypted, on this PC." : $"Check the key with {Info.Company}, then save it, encrypted, on this PC.";
    public string ModelEyebrow => Info.IsCustom ? "MODEL" : $"{Info.Name.ToUpperInvariant()} MODEL";

    public IReadOnlyList<ModelChoice> ModelItems => Info.Models;
    public bool ShowModelList => Info.Models.Count > 0;
    /// <summary>Models change often, so every provider but Claude also takes a model name typed in.</summary>
    public bool ShowModelId => !Info.IsClaude;
    public bool ShowAddress => Info.IsCustom;

    public ModelChoice? SelectedModel
    {
        get => Info.Models.FirstOrDefault(m => m.Id == _s.Model);
        set
        {
            if (value is null) return;
            _s.Model = value.Id;
            KeyEntry.ModelId = _s.Model;
            OnPropertyChanged(nameof(ModelId));
            Changed(nameof(SelectedModel));
        }
    }

    public string ModelId
    {
        get => _s.Model;
        set
        {
            _s.Model = (value ?? "").Trim();
            KeyEntry.ModelId = _s.Model;
            OnPropertyChanged(nameof(SelectedModel));
            Changed(nameof(ModelId));
        }
    }

    public string BaseUrl
    {
        get => _s.BaseUrl;
        set
        {
            _s.BaseUrl = (value ?? "").Trim();
            KeyEntry.BaseUrl = _s.BaseUrl;
            Changed(nameof(BaseUrl));
        }
    }

    public string ThinkTip => $"Think before replying. On: the AI spends more effort on each reply, which is slower to start but better on hard or technical questions. Off (the default): replies begin as soon as they can.";
    public string ThinkBlurb => "Slower to start, better on hard or technical questions. Off: replies begin sooner.";

    public SpeechChoice? SelectedSpeech
    {
        get => SpeechItems.FirstOrDefault(s => s.Value == _s.SpeechAccuracy);
        set { if (value is null) return; _s.SpeechAccuracy = value.Value; Changed(nameof(SelectedSpeech)); }
    }

    // -- API key ------------------------------------------------------------------------------

    [ObservableProperty] private string _maskedKey = NoKeyText;
    [ObservableProperty] private bool _isEditingKey;

    public bool HasKey => MaskedKey != NoKeyText;

    partial void OnMaskedKeyChanged(string value) => OnPropertyChanged(nameof(HasKey));

    [RelayCommand]
    private void ChangeKey()
    {
        KeyEntry.Reset();
        IsEditingKey = true;
    }

    [RelayCommand] private void CancelChangeKey() => IsEditingKey = false;

    [RelayCommand]
    private void RemoveKey()
    {
        MaskedKey = NoKeyText;
        _keyChanged(null);
    }
}
