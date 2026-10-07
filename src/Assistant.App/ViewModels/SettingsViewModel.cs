using Assistant.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Assistant.App.ViewModels;

public sealed record SpeechChoice(SpeechAccuracy Value, string Name, string Blurb, string Tip = "");

/// <summary>One option of a segmented control, with the explanation shown when the mouse rests on it.</summary>
public sealed record ChoiceItem(string Label, string Tip)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Settings _s;
    private readonly Action _changed;

    public SettingsViewModel(Settings settings, Action changed, KeyEntryViewModel keyEntry, string? currentKey, Action<string?> keyChanged)
    {
        _s = settings;
        _changed = changed;
        KeyEntry = keyEntry;
        _keyChanged = keyChanged;
        _maskedKey = currentKey is null ? "No key saved" : ApiKeyStore.Mask(currentKey);
        keyEntry.Accepted = key =>
        {
            MaskedKey = ApiKeyStore.Mask(key);
            IsEditingKey = false;
            _keyChanged(key);
        };
    }

    private readonly Action<string?> _keyChanged;

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

    public static IReadOnlyList<ModelChoice> ModelItems => Models.All;
    public static IReadOnlyList<SpeechChoice> SpeechItems { get; } = new[]
    {
        new SpeechChoice(SpeechAccuracy.Fast, "Fast", "75 MB download, lowest accuracy",
            "The smallest speech model. Keeps up on slower PCs, but makes more mistakes."),
        new SpeechChoice(SpeechAccuracy.Balanced, "Balanced", "140 MB download, good for most calls",
            "Good accuracy for most calls on a typical PC."),
        new SpeechChoice(SpeechAccuracy.Accurate, "Accurate", "470 MB download, best accuracy, slower",
            "The most accurate. Needs a fast PC, and may fall behind live speech on a slow one."),
    };

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

    public ModelChoice? SelectedModel
    {
        get => Models.All.FirstOrDefault(m => m.Id == _s.Model);
        set { if (value is null) return; _s.Model = value.Id; Changed(nameof(SelectedModel)); }
    }

    public SpeechChoice? SelectedSpeech
    {
        get => SpeechItems.FirstOrDefault(s => s.Value == _s.SpeechAccuracy);
        set { if (value is null) return; _s.SpeechAccuracy = value.Value; Changed(nameof(SelectedSpeech)); }
    }

    // -- API key ------------------------------------------------------------------------------

    [ObservableProperty] private string _maskedKey;
    [ObservableProperty] private bool _isEditingKey;

    public bool HasKey => MaskedKey != "No key saved";

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
        MaskedKey = "No key saved";
        _keyChanged(null);
    }
}
