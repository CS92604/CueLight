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
    public const string NoRegionText = "Pick a chat or doc for Claude to read when it changes";

    private readonly Engine _engine;
    private readonly Settings _settings;
    private readonly Func<string, Task> _copy;
    private readonly Func<Task> _pickRegion;
    private readonly Action _openSettings;
    private readonly Action _settingsChanged;
    private readonly Action _retrySpeech;
    private string _raw = "";

    public MainViewModel(Engine engine, Settings settings, Func<string, Task> copy, Func<Task> pickRegion,
        Action openSettings, Action settingsChanged, Action retrySpeech)
    {
        _engine = engine;
        _settings = settings;
        _copy = copy;
        _pickRegion = pickRegion;
        _openSettings = openSettings;
        _settingsChanged = settingsChanged;
        _retrySpeech = retrySpeech;
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

    public bool HasTurns => Turns.Count > 0;
    public bool HasSections => Sections.Count > 0;
    public bool IsListening => Status == StatusKind.Listening;
    public bool IsThinking => Status == StatusKind.Thinking || Status == StatusKind.Preparing;
    public bool IsError => Status == StatusKind.Error;

    partial void OnStatusChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(IsError));
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
            case EngineEventKind.RegionChanged:
                HasRegion = e.Region is not null;
                RegionText = e.Region is { } r ? $"Watching {r}" : NoRegionText;
                break;
        }
    }

    private void Render(bool final)
    {
        var parsed = SuggestionParser.Parse(_raw);
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
        var hint = Hint.Trim();
        Hint = "";
        _engine.Request(Trigger.None, hint.Length > 0 ? hint : null);
    }

    /// <summary>Panic: re-read the text area right now and reply. Asks for an area first if there isn't one.</summary>
    [RelayCommand]
    private async Task Panic()
    {
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
