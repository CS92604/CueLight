using System.Collections.ObjectModel;
using Cuelight.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cuelight.App.ViewModels;

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
    public const string HearingText = "LISTENING";
    public const string WaitingText = "Waiting for speech";

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

    public void Dispose()
    {
        _engine.Event -= _handler;
        _liveTimer?.Stop();
    }

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
    [ObservableProperty] private bool _isHearing;
    [ObservableProperty] private string _liveText = "";
    private Speaker _liveWho = Speaker.Them;
    [ObservableProperty] private string _costText = "$0.00";
    [ObservableProperty] private string _costTip = CostTipFor(new UsageSnapshot(0, 0, 0, 0, 0, 0m, false));

    public bool HasTurns => Turns.Count > 0;

    // The line at the end of the conversation that shows words as they are being said, with animated dots.
    public bool HasLiveText => LiveText.Length > 0;
    public bool ShowLive => IsRecording && (IsHearing || HasLiveText);
    public string LiveWho => _liveWho == Speaker.Me ? "You" : "Them";
    public bool LiveIsMe => _liveWho == Speaker.Me;
    public bool ShowTranscript => HasTurns || ShowLive;
    public bool ShowNothingYet => !ShowTranscript;

    private void RaiseLiveProperties()
    {
        OnPropertyChanged(nameof(HasLiveText));
        OnPropertyChanged(nameof(ShowLive));
        OnPropertyChanged(nameof(LiveWho));
        OnPropertyChanged(nameof(LiveIsMe));
        OnPropertyChanged(nameof(ShowTranscript));
        OnPropertyChanged(nameof(ShowNothingYet));
    }

    /// <summary>
    /// How long between one live word showing and the next. The speech engine hands over a few words at a time; showing
    /// them one by one at this pace (a little faster when many are waiting) makes the line read as live speech, not as
    /// blocks. Zero shows each batch at once.
    /// </summary>
    public TimeSpan LiveWordInterval { get; set; } = TimeSpan.FromMilliseconds(35);

    private string[] _liveWords = Array.Empty<string>();   // the words the engine has so far
    private int _liveShown;                                // how many of them are on screen
    private DispatcherTimer? _liveTimer;

    /// <summary>Takes the live words (and who is saying them) from the engine.</summary>
    private void RefreshLive()
    {
        var them = _engine.LiveText(Speaker.Them);
        var me = _engine.LiveText(Speaker.Me);
        string? text = them ?? me;
        _liveWho = them is not null ? Speaker.Them : me is not null ? Speaker.Me : _engine.HearingWho ?? Speaker.Them;
        SetLiveWords(text ?? "");
        RaiseLiveProperties();
    }
    partial void OnLiveTextChanged(string value) => RaiseLiveProperties();

    private void SetLiveWords(string text)
    {
        _liveWords = text.Length == 0 ? Array.Empty<string>() : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (_liveWords.Length == 0 || LiveWordInterval <= TimeSpan.Zero) _liveShown = _liveWords.Length;
        else _liveShown = Math.Clamp(_liveShown, 1, _liveWords.Length);   // words already showing stay (even if reworded); the first shows at once
        ShowLiveWords();
        if (_liveShown >= _liveWords.Length) { _liveTimer?.Stop(); return; }
        _liveTimer ??= new DispatcherTimer();
        _liveTimer.Interval = LiveWordInterval;
        _liveTimer.Tick -= OnLiveTick;
        _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();
    }

    /// <summary>How many of the live words the engine has are not on screen yet.</summary>
    internal int LiveWordsWaiting => _liveWords.Length - _liveShown;

    private void OnLiveTick(object? sender, EventArgs e) => AdvanceLiveWords();

    /// <summary>Show the next live word, or several when many are waiting. The timer calls this.</summary>
    internal void AdvanceLiveWords()
    {
        int waiting = _liveWords.Length - _liveShown;
        if (waiting <= 0) { _liveTimer?.Stop(); return; }
        _liveShown += Math.Clamp(waiting / 6, 1, waiting);   // a long queue is worked off faster, so it never falls far behind
        ShowLiveWords();
        if (_liveShown >= _liveWords.Length) _liveTimer?.Stop();
    }

    private void ShowLiveWords() => LiveText = string.Join(' ', _liveWords.Take(_liveShown));
    public bool HasSections => Sections.Count > 0;

    public string EmptyHint => TypeEnabled
        ? "Suggestions will appear here as people talk or your text area changes."
        : "Suggestions will appear here as people talk.";

    // What the status area shows. While recording is off it says so and goes quiet, whatever the
    // speech model or the AI last reported; that comes back when recording does.
    public string StatusLine => !IsRecording ? PausedText : ShowHearing ? HearingText : StatusText;

    /// <summary>Someone is speaking right now, so the status says LISTENING. Problems and set-up messages still win.</summary>
    public bool ShowHearing => IsRecording && IsHearing && (Status == StatusKind.Listening || Status == StatusKind.Thinking);
    public bool IsListening => IsRecording && Status == StatusKind.Listening;
    public bool IsThinking => IsRecording && (Status == StatusKind.Thinking || Status == StatusKind.Preparing);
    public bool IsError => IsRecording && Status == StatusKind.Error;
    public bool ProgressVisible => IsRecording && ShowProgress;
    public bool RetryVisible => IsRecording && CanRetry;

    // Hover text for the buttons Recording turns off: when it's off they say why.
    public string SendTip => IsRecording
        ? "Get suggestions now. The AI replies to what has been said (and to the text area, if Type is on). Anything typed in the box is used as direction."
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
        OnPropertyChanged(nameof(ShowHearing));
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(ProgressVisible));
        OnPropertyChanged(nameof(RetryVisible));
    }

    partial void OnStatusChanged(StatusKind value) => RaiseStatusProperties();
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnIsHearingChanged(bool value)
    {
        RaiseStatusProperties();
        RaiseLiveProperties();
    }
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
        RaiseLiveProperties();
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
        Set(StatusKind.Listening, _engine.Options.AutoSuggest ? WaitingText : WaitingText + " · suggestions on request");
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
                OnPropertyChanged(nameof(ShowTranscript));
                OnPropertyChanged(nameof(ShowNothingYet));
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
            case EngineEventKind.Hearing:
                IsHearing = _engine.IsHearingSpeech;
                RefreshLive();
                break;
            case EngineEventKind.Live:
                RefreshLive();
                break;
            case EngineEventKind.Usage:
                var usage = _engine.Usage.Snapshot();
                CostText = CostLabel(usage);
                CostTip = CostTipFor(usage, Providers.Get(_settings.Provider));
                break;
            case EngineEventKind.RegionChanged:
                HasRegion = e.Region is not null;
                RegionText = e.Region is { } r ? $"Watching {r}" : NoRegionText;
                break;
        }
    }

    /// <summary>The running cost as shown in the corner: an estimate, so it says "≈" (and "+" when part of the use couldn't be priced).</summary>
    public static string CostLabel(UsageSnapshot u)
    {
        if (u.Requests == 0) return "$0.00";
        if (u.Cost == 0 && u.Unpriced) return Tokens(u.TotalInput + u.Output);   // no price is known for the model: count tokens instead
        var amount = u.Cost < 0.005m ? "<$0.01" : "$" + u.Cost.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
        return "≈ " + amount + (u.Unpriced ? "+" : "");
    }

    private static string Tokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M tokens" : n >= 1_000 ? $"{n / 1_000.0:0.#}k tokens" : $"{n} tokens";

    public static string CostTipFor(UsageSnapshot u, ProviderInfo? provider = null)
    {
        provider ??= Providers.Get(Provider.Claude);
        string who = provider.IsCustom ? "the AI" : provider.Name;
        string exact = provider.IsClaude
            ? "Your Claude Console (console.anthropic.com) shows the exact amount, and lets you set a monthly spending limit."
            : provider.IsCustom
                ? "Your provider's own dashboard shows the exact amount."
                : $"Your {provider.Company} account ({provider.KeyHost}) shows the exact amount.";
        if (u.Requests == 0)
            return $"Estimated cost of {who}'s suggestions since you opened the app. Nothing has been sent yet. " + exact;
        var share = (int)Math.Round(u.CachedShare * 100);
        string prices = u.Unpriced
            ? "No price is known for some of the models used, so those are counted in tokens only and the total is incomplete. "
            : provider.IsCustom ? "" : $"This is worked out from {provider.Company}'s list prices. ";
        return $"Estimated cost of {who}'s suggestions since you opened the app: {u.Requests} request{(u.Requests == 1 ? "" : "s")}. "
             + $"{share}% of what {who} read came from its memory of earlier requests, which costs far less. "
             + prices + exact;
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
        RaiseLiveProperties();
    }

    [RelayCommand] private Task SelectRegion() => _pickRegion();
    [RelayCommand] private void ClearRegion() => _engine.SetRegion(null);
    [RelayCommand] private void OpenSettings() => _openSettings();
    [RelayCommand] private void RetrySpeech() => _retrySpeech();
}
