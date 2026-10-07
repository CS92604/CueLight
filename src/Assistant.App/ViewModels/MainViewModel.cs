using System.Collections.ObjectModel;
using Assistant.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Assistant.App.ViewModels;

public enum StatusKind { Idle, Listening, Thinking, Preparing, Error }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxTurnsShown = 40;
    public const string NoRegionText = "Select the exact area to watch: a chat, email or document";

    private readonly Engine _engine;
    private readonly Settings _settings;
    private readonly Func<string, Task> _copy;
    private readonly Func<Task> _pickRegion;
    private readonly Action _openSettings;
    private readonly Action _settingsChanged;
    private readonly Action _retrySpeech;
    private readonly Action<bool>? _recordingChanged;
    private string _raw = "";

    public const string PausedText = "Recording is off. Nothing is being heard, watched or sent.";

    public MainViewModel(Engine engine, Settings settings, Func<string, Task> copy, Func<Task> pickRegion,
        Action openSettings, Action settingsChanged, Action retrySpeech, Action<bool>? recordingChanged = null)
    {
        _engine = engine;
        _settings = settings;
        _copy = copy;
        _pickRegion = pickRegion;
        _openSettings = openSettings;
        _settingsChanged = settingsChanged;
        _retrySpeech = retrySpeech;
        _recordingChanged = recordingChanged;
        _typeEnabled = settings.TypeEnabled;
        _autoSuggest = settings.AutoSuggest;
        _pinOnTop = settings.AlwaysOnTop;
        _handler = e => Dispatcher.UIThread.Post(() => Handle(e));
        engine.Event += _handler;
    }

    private readonly Action<EngineEvent> _handler;

    public void Dispose() => _engine.Event -= _handler;

    public ObservableCollection<TurnVm> Turns { get; } = new();
    public ObservableCollection<SectionVm> Sections { get; } = new();

    [ObservableProperty] private StatusKind _status = StatusKind.Idle;
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _hasRegion;
    [ObservableProperty] private string _regionText = NoRegionText;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _autoSuggest;
    [ObservableProperty] private bool _pinOnTop;
    [ObservableProperty] private bool _isRecording = true;
    [ObservableProperty] private bool _typeEnabled;

    public bool HasTurns => Turns.Count > 0;
    public bool HasSections => Sections.Count > 0;

    public string EmptyHint => TypeEnabled
        ? "Suggestions will appear here as people talk or your text area changes."
        : "Suggestions will appear here as people talk.";

    // What the status area shows. While recording is off it says so and goes quiet, whatever the
    // speech model or Claude last reported; that comes back when recording does.
    public string StatusLine => IsRecording ? StatusText : PausedText;
    public bool IsListening => IsRecording && Status == StatusKind.Listening;
    public bool IsThinking => IsRecording && (Status == StatusKind.Thinking || Status == StatusKind.Preparing);
    public bool IsError => IsRecording && Status == StatusKind.Error;
    public bool ProgressVisible => IsRecording && ShowProgress;
    public bool RetryVisible => IsRecording && CanRetry;

    // Hover text for the buttons Recording turns off: when it's off they say why.
    public string SendTip => IsRecording
        ? "Get suggestions now. Claude replies to what has been said (and to the text area, if Type is on). Anything typed in the box is used as direction."
        : "Recording is off. Turn it on to get suggestions.";

    public string PanicTip => IsRecording
        ? "Panic. Reads the text area right now, exactly as it looks at this moment, and gives you a reply to type. Works even with Auto-suggest off. If no area is selected yet, it asks you to pick one."
        : "Recording is off. Turn it on to use Panic.";

    public string RegenerateTip => IsRecording
        ? "Regenerate. Redo this reply with a different take. Type a direction in the box first, like “shorter”, to steer it."
        : "Recording is off. Turn it on to use Regenerate.";

    private void RaiseStatusProperties()
    {
        OnPropertyChanged(nameof(SendTip));
        OnPropertyChanged(nameof(PanicTip));
        OnPropertyChanged(nameof(RegenerateTip));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(ProgressVisible));
        OnPropertyChanged(nameof(RetryVisible));
    }

    partial void OnStatusChanged(StatusKind value) => RaiseStatusProperties();
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnShowProgressChanged(bool value) => OnPropertyChanged(nameof(ProgressVisible));
    partial void OnCanRetryChanged(bool value) => OnPropertyChanged(nameof(RetryVisible));

    partial void OnIsRecordingChanged(bool value)
    {
        _engine.SetPaused(!value);
        if (!value && Status == StatusKind.Thinking)
        {
            // A reply that was cut off mid-way isn't worth keeping (its options can't be trusted).
            Sections.Clear();
            OnPropertyChanged(nameof(HasSections));
            SetListening();
        }
        RaiseStatusProperties();
        _recordingChanged?.Invoke(value);
    }

    partial void OnTypeEnabledChanged(bool value)
    {
        _settings.TypeEnabled = value;
        OnPropertyChanged(nameof(EmptyHint));
        _engine.SetTextEnabled(value);
        Render(final: !IsThinking); // TYPE replies come and go with the switch
        _settingsChanged();
    }

    partial void OnAutoSuggestChanged(bool value)
    {
        _settings.AutoSuggest = value;
        _settingsChanged();
    }

    partial void OnPinOnTopChanged(bool value)
    {
        _settings.AlwaysOnTop = value;
        _settingsChanged();
    }

    // -- status from the host (speech model, audio) ------------------------------------------

    public void SetListening()
    {
        ShowProgress = false;
        CanRetry = false;
        Set(StatusKind.Listening, _engine.Options.AutoSuggest ? "Listening" : "Listening · suggestions on request");
    }

    public void SetPreparing(string text, double? progress)
    {
        CanRetry = false;
        ShowProgress = progress.HasValue;
        Progress = (progress ?? 0) * 100;
        Set(StatusKind.Preparing, text);
    }

    public void SetProblem(string text, bool canRetry = false)
    {
        ShowProgress = false;
        CanRetry = canRetry;
        Set(StatusKind.Error, text);
    }

    public void SetIdle(string text) => Set(StatusKind.Idle, text);

    private void Set(StatusKind kind, string text)
    {
        Status = kind;
        StatusText = text;
    }

    // -- engine events ------------------------------------------------------------------------

    private void Handle(EngineEvent e)
    {
        switch (e.Kind)
        {
            case EngineEventKind.Turn:
                Turns.Add(new TurnVm(e.Speaker ?? Speaker.Them, e.Text ?? ""));
                while (Turns.Count > MaxTurnsShown) Turns.RemoveAt(0);
                OnPropertyChanged(nameof(HasTurns));
                break;
            case EngineEventKind.SuggestStart:
                _raw = "";
                Sections.Clear();
                OnPropertyChanged(nameof(HasSections));
                Set(StatusKind.Thinking, e.Text ?? "Thinking…");
                break;
            case EngineEventKind.Chunk:
                _raw += e.Text;
                Render(final: false);
                break;
            case EngineEventKind.SuggestEnd:
                Render(final: true);
                SetListening();
                break;
            case EngineEventKind.Status:
                StatusText = e.Text ?? "";
                break;
            case EngineEventKind.Error:
                Set(StatusKind.Error, e.Text ?? "Something went wrong.");
                break;
            case EngineEventKind.Recovered:
                // Whatever reported this problem works again (a device came back, the screen unlocked).
                if (Status == StatusKind.Error && StatusText == e.Text) SetListening();
                break;
            case EngineEventKind.RegionChanged:
                HasRegion = e.Region is not null;
                RegionText = e.Region is { } r ? $"Watching {r}" : NoRegionText;
                break;
        }
    }

    private void Render(bool final)
    {
        var parsed = SuggestionParser.Parse(_raw).Where(sec => TypeEnabled || sec.Kind != SectionKind.Type).ToList();
        Sections.Clear();
        for (int s = 0; s < parsed.Count; s++)
        {
            var opts = parsed[s].Options.Select((text, i) =>
                new OptionVm(text, final || s < parsed.Count - 1 || i < parsed[s].Options.Count - 1, _copy));
            Sections.Add(new SectionVm(parsed[s].Kind, opts));
        }
        OnPropertyChanged(nameof(HasSections));
    }

    // -- commands -----------------------------------------------------------------------------

    [RelayCommand]
    private void Suggest()
    {
        if (!IsRecording) return;
        var hint = Hint.Trim();
        Hint = "";
        _engine.Request(Trigger.None, hint.Length > 0 ? hint : null);
    }

    /// <summary>Panic: re-read the text area right now and reply. Asks for an area first if there isn't one.</summary>
    [RelayCommand]
    private async Task Panic()
    {
        if (!IsRecording || !TypeEnabled) return;
        if (_engine.Region is null)
        {
            await _pickRegion();
            if (_engine.Region is null) return; // picker cancelled
        }
        _engine.Panic();
    }

    /// <summary>Redo the current reply. Anything typed in the box becomes the new direction ("shorter", ...).</summary>
    [RelayCommand]
    private void Regenerate()
    {
        if (!IsRecording) return;
        var hint = Hint.Trim();
        Hint = "";
        _engine.Regenerate(hint.Length > 0 ? hint : null);
    }

    [RelayCommand]
    private void ClearChat()
    {
        _engine.ClearConversation();
        Turns.Clear();
        Sections.Clear();
        OnPropertyChanged(nameof(HasTurns));
        OnPropertyChanged(nameof(HasSections));
    }

    [RelayCommand] private Task SelectRegion() => _pickRegion();
    [RelayCommand] private void ClearRegion() => _engine.SetRegion(null);
    [RelayCommand] private void OpenSettings() => _openSettings();
    [RelayCommand] private void RetrySpeech() => _retrySpeech();
}
