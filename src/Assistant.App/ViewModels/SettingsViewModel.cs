using Assistant.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Assistant.App.ViewModels;

public sealed record SpeechChoice(SpeechAccuracy Value, string Name, string Blurb);

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

    public static IReadOnlyList<string> ProfessionalismItems { get; } = new[] { "Very casual", "Casual", "Professional", "Formal" };
    public static IReadOnlyList<string> ProficiencyItems { get; } = new[] { "Simple", "Everyday", "Fluent", "Advanced" };
    public static IReadOnlyList<string> ToneItems { get; } = new[] { "Warm", "Neutral", "Direct", "Diplomatic", "Confident" };
    public static IReadOnlyList<string> LengthItems { get; } = new[] { "Brief", "Short", "Detailed" };
    public static IReadOnlyList<string> OptionItems { get; } = new[] { "1", "2", "3" };
    public static IReadOnlyList<ModelChoice> ModelItems => Models.All;
    public static IReadOnlyList<SpeechChoice> SpeechItems { get; } = new[]
    {
        new SpeechChoice(SpeechAccuracy.Fast, "Fast", "75 MB download, lowest accuracy"),
        new SpeechChoice(SpeechAccuracy.Balanced, "Balanced", "140 MB download, good for most calls"),
        new SpeechChoice(SpeechAccuracy.Accurate, "Accurate", "470 MB download, best accuracy, slower"),
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
    public bool UseMicrophone { get => _s.UseMicrophone; set { _s.UseMicrophone = value; Changed(nameof(UseMicrophone)); } }

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
