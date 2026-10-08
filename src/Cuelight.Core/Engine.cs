using System.Diagnostics;

namespace Cuelight.Core;

public enum EngineEventKind { Turn, SuggestStart, Chunk, SuggestEnd, Status, Error, RegionChanged, ModeChanged, Recovered, Usage, Hearing, Live }

public sealed record EngineEvent(
    EngineEventKind Kind, string? Text = null, Speaker? Speaker = null, Region? Region = null);

public sealed class EngineOptions
{
    public bool AutoSuggest { get; set; } = true;
    /// <summary>Ignore short backchannels ("mm-hmm", "okay") for automatic suggestions.</summary>
    public int AutoMinWords { get; set; } = 4;
    /// <summary>Wait this long after the last trigger before asking Claude. Speech has already been cut at a pause
    /// and takes a moment to transcribe, so this only needs to merge triggers that arrive almost together.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);
    /// <summary>
    /// If Claude found nothing to reply to after the other person spoke, and then nobody says anything for this
    /// long, ask once more, telling it the pause is long: they are probably waiting for an answer. (A quiz
    /// question, or a sentence left hanging, can look like nothing to reply to until the silence goes on.)
    /// <see cref="Timeout.InfiniteTimeSpan"/> turns this off.
    /// </summary>
    public TimeSpan PauseFollowUp { get; set; } = TimeSpan.FromSeconds(4);
    /// <summary>Max time to hold a spoken request while the watched text is still changing.</summary>
    public TimeSpan MergeHold { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>
    /// How much conversation, at most, goes with each request (about 5,000 words: over half an hour of talk).
    /// Every request pays for all of it, so a long call can't be left to grow without limit.
    /// </summary>
    public int TranscriptChars { get; set; } = 30_000;
    /// <summary>
    /// When the conversation passes <see cref="TranscriptChars"/> the oldest turns are dropped until about this
    /// much is left. Trimming in a jump, rather than a little every time, keeps the start of what Claude is sent
    /// the same for many requests in a row, so Claude's prompt cache can go on reading it cheaply.
    /// </summary>
    public int TranscriptKeepChars { get; set; } = 20_000;
}

/// <summary>
/// Holds the conversation and turns it into streamed Claude suggestions.
///
/// Two things can trigger a suggestion: new speech from "Them", and the watched screen region
/// changing. Requests are coalesced: a newer trigger supersedes one still waiting or streaming,
/// and the channels that triggered are merged, so if speech and on-screen text change together
/// Claude is asked for both a SAY and a TYPE reply.
///
/// Two switches gate everything: <see cref="Paused"/> (recording off: nothing is watched and nothing
/// is sent) and <see cref="TextEnabled"/> (TYPE off: the region is ignored and only speech is used).
///
/// Each request carries the recent conversation but only the current picture of the watched region;
/// earlier pictures are never kept or resent. <see cref="Panic"/> forces an immediate re-read of
/// the region, and <see cref="Regenerate"/> redoes the last reply with a different take.
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly ISuggester _suggester;
    private readonly IScreenCapture _capture;
    private readonly ITextReader? _textReader;
    private readonly Func<Region, Action, Action<string>, IScreenWatcher> _watcherFactory;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();

    private Trigger _kinds;
    private string? _hint;
    private bool _immediate;        // skip the merge hold (Panic / Regenerate)
    private bool _redo;             // the pending run redoes the last reply
    private Trigger _lastKinds;     // what the most recent run was asked, for Regenerate
    private string? _lastHint;
    private string _lastReply = "";
    private int _turnSeq;           // counts turns added, so a follow-up can tell whether anything was said since
    private readonly List<string> _rejected = new();   // replies the user has regenerated away
    private double? _deadline;      // seconds on _clock
    private double _queuedAt;
    private CancellationTokenSource? _runCts;
    private int _clearEpoch;
    private string? _lastScreenText;   // the words last sent from the watched region (Fast screen reading), to skip a repeat
    private bool _warnedNoReader;
    private readonly object _watcherLock = new();
    private IScreenWatcher? _watcher;
    private Task? _loop;

    public Engine(EngineOptions options, ISuggester suggester, IScreenCapture capture,
        Func<Region, Action, Action<string>, IScreenWatcher>? watcherFactory = null, ITextReader? textReader = null)
    {
        Options = options;
        _suggester = suggester;
        _capture = capture;
        _textReader = textReader;
        _watcherFactory = watcherFactory ?? ((r, onChange, onError) =>
        {
            string? failure = null;
            return new ScreenWatcher(r, capture, onChange,
                msg => { failure = msg; onError(msg); },
                onRecovered: () => { if (failure is { } f) RaiseRecovered(f); failure = null; });
        });
        Settings = new Settings();
    }

    public EngineOptions Options { get; }
    public Settings Settings { get; set; }
    public Conversation Conversation { get; } = new();

    /// <summary>What the Claude requests of this session have used so far, in tokens and estimated dollars.</summary>
    public UsageMeter Usage { get; } = new();
    public Region? Region { get; private set; }

    /// <summary>Recording is off: nothing is watched, nothing triggers, nothing is sent.</summary>
    public bool Paused { get; private set; }

    /// <summary>TYPE is on: the selected region is watched and included in requests.</summary>
    public bool TextEnabled { get; private set; } = true;

    private readonly HashSet<Speaker> _speaking = new();

    /// <summary>Someone is speaking right now (on the speakers, or into the microphone if it is on).</summary>
    public bool IsHearingSpeech { get { lock (_gate) return !Paused && _speaking.Count > 0; } }

    private readonly Dictionary<Speaker, string> _liveText = new();

    /// <summary>The words of what <paramref name="who"/> is saying right now, as far as they are known (a preview that the real
    /// transcript replaces), or null.</summary>
    public string? LiveText(Speaker who) { lock (_gate) return _liveText.GetValueOrDefault(who); }

    /// <summary>Who is speaking right now, if anyone: the other person when both are.</summary>
    public Speaker? HearingWho
    {
        get { lock (_gate) return Paused || _speaking.Count == 0 ? null : _speaking.Contains(Speaker.Them) ? Speaker.Them : Speaker.Me; }
    }

    /// <summary>Show the words so far of what someone is saying (null or empty: nothing to show). Called by the audio side.</summary>
    public void SetLive(Speaker who, string? text)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        bool changed;
        lock (_gate)
        {
            if (Paused) text = null;
            var old = _liveText.GetValueOrDefault(who);
            changed = old != text;
            if (text is null) _liveText.Remove(who); else _liveText[who] = text;
        }
        if (changed) Emit(new EngineEvent(EngineEventKind.Live, text, who));
    }

    /// <summary>Called by the audio side when a person starts or stops speaking.</summary>
    public void SetSpeaking(Speaker who, bool speaking)
    {
        bool before, after;
        lock (_gate)
        {
            before = !Paused && _speaking.Count > 0;
            if (speaking) _speaking.Add(who); else _speaking.Remove(who);
            after = !Paused && _speaking.Count > 0;
        }
        if (before != after) Emit(new EngineEvent(EngineEventKind.Hearing, Speaker: who));
    }

    /// <summary>True while the screen region is actually being polled.</summary>
    public bool IsWatching => Region is not null && TextEnabled && !Paused;

    /// <summary>Raised from background threads; marshal to the UI thread before touching controls.</summary>
    public event Action<EngineEvent>? Event;

    private double Now => _clock.Elapsed.TotalSeconds;
    private void Emit(EngineEvent e) => Event?.Invoke(e);

    public void Start() => _loop ??= Task.Run(LoopAsync);

    // -- watched region ---------------------------------------------------------------------

    public void SetRegion(Region? region)
    {
        lock (_gate) { Region = region; _lastScreenText = null; }
        Emit(new EngineEvent(EngineEventKind.RegionChanged, Region: region));
        ReconcileWatcher();
    }

    /// <summary>Recording on/off. Pausing cancels anything in flight, drops anything pending and stops watching.</summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (Paused == paused) return;
            Paused = paused;
            if (paused)
            {
                _speaking.Clear();
                _liveText.Clear();
                _clearEpoch++;                    // the cancelled run must not hand its debt to the next one
                _runCts?.Cancel();
                _deadline = null;
                _kinds = Trigger.None;
                _hint = null;
                _immediate = _redo = false;
            }
        }
        ReconcileWatcher();
        Emit(new EngineEvent(EngineEventKind.ModeChanged));
        Emit(new EngineEvent(EngineEventKind.Hearing)); // the "listening" display follows recording
        if (paused) Emit(new EngineEvent(EngineEventKind.Live));
    }

    /// <summary>TYPE on/off. Off ignores the region entirely; the region itself is kept for when it's switched back on.</summary>
    public void SetTextEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (TextEnabled == enabled) return;
            TextEnabled = enabled;
            if (!enabled)
            {
                _kinds &= ~(Trigger.Text | Trigger.Forced);
                if (_kinds == Trigger.None) { _deadline = null; _hint = null; _immediate = _redo = false; }
            }
        }
        ReconcileWatcher();
        Emit(new EngineEvent(EngineEventKind.ModeChanged));
    }

    /// <summary>Make the running watcher match what should be watched right now.</summary>
    private void ReconcileWatcher()
    {
        lock (_watcherLock)
        {
            IScreenWatcher? old;
            Region? wanted;
            lock (_gate) { old = _watcher; _watcher = null; wanted = IsWatching ? Region : null; }
            old?.Dispose();
            if (wanted is not { } r) return;
            var watcher = _watcherFactory(r, OnScreenChanged, msg => Emit(new EngineEvent(EngineEventKind.Error, msg)));
            lock (_gate) _watcher = watcher;
            watcher.Start();
        }
    }

    /// <summary>Surface a problem from a background component (audio device, speech model, ...).</summary>
    public void RaiseError(string message) => Emit(new EngineEvent(EngineEventKind.Error, message));

    /// <summary>The thing that reported <paramref name="failedMessage"/> works again; the UI clears that message.</summary>
    public void RaiseRecovered(string failedMessage) => Emit(new EngineEvent(EngineEventKind.Recovered, failedMessage));

    public void OnScreenChanged()
    {
        if (Options.AutoSuggest && IsWatching) Request(Trigger.Text, delay: Options.Debounce);
    }

    // -- inputs -----------------------------------------------------------------------------

    public void AddTurn(Speaker speaker, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        Conversation.Add(speaker, text);
        Interlocked.Increment(ref _turnSeq);
        Emit(new EngineEvent(EngineEventKind.Turn, text, speaker));
        if (speaker == Speaker.Them && Options.AutoSuggest
            && text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= Options.AutoMinWords)
            Request(Trigger.Speech, delay: Options.Debounce);
    }

    /// <summary>Ask for a suggestion. With <see cref="Trigger.None"/> this is a manual request.</summary>
    public void Request(Trigger kinds = Trigger.None, string? hint = null, TimeSpan delay = default,
        bool immediate = false)
    {
        lock (_gate)
        {
            if (Paused) return;
            _runCts?.Cancel(); // supersedes anything in flight
            _kinds |= kinds == Trigger.None ? Trigger.Manual : kinds;
            if (_deadline is null) _queuedAt = Now;
            _deadline = Now + delay.TotalSeconds;
            _hint = hint ?? _hint;
            _immediate |= immediate;
        }
        _signal.Release();
    }

    /// <summary>
    /// The Panic button: right now, re-read the watched region and reply to it (and to the last
    /// thing said, if it needs an answer). Skips the debounce, the settle wait and the auto-suggest
    /// switch. Returns false, and says why, when there is no region to read.
    /// </summary>
    public bool Panic(string? hint = null)
    {
        if (Paused)
        {
            Emit(new EngineEvent(EngineEventKind.Status, "Recording is off. Turn it on to use Panic."));
            return false;
        }
        if (!TextEnabled)
        {
            Emit(new EngineEvent(EngineEventKind.Status, "Turn Type on and pick a text area, then Panic can read it."));
            return false;
        }
        if (Region is null)
        {
            Emit(new EngineEvent(EngineEventKind.Status, "Pick a text area first, then Panic can read it."));
            return false;
        }
        Request(Trigger.Manual | Trigger.Forced, hint, immediate: true);
        return true;
    }

    /// <summary>
    /// Redo the last reply: the same kind of request, with the current transcript and settings, and
    /// the replies already thrown away passed along so Claude takes a different angle. A new
    /// <paramref name="hint"/> replaces the previous one; without one the previous hint carries over.
    /// </summary>
    public void Regenerate(string? hint = null)
    {
        lock (_gate)
        {
            if (Paused) return;
            _runCts?.Cancel();
            // Cancelling takes effect inside this lock, so a stale chunk can't overwrite the reset below.
            if (_lastReply.Length > 0 && !_lastReply.StartsWith('('))
            {
                _rejected.Add(_lastReply.Length > MaxRejectedChars ? _lastReply[..MaxRejectedChars] : _lastReply);
                while (_rejected.Count > MaxRejected) _rejected.RemoveAt(0);
            }
            _lastReply = "";
            _kinds |= _lastKinds == Trigger.None ? Trigger.Manual : _lastKinds;
            _hint = hint ?? _hint ?? _lastHint;
            _redo = true;
            _immediate = true;
            if (_deadline is null) _queuedAt = Now;
            _deadline = Now;
        }
        _signal.Release();
    }

    private const int MaxRejected = 3;
    private const int MaxRejectedChars = 1500;

    public void ClearConversation()
    {
        lock (_gate)
        {
            _clearEpoch++;
            _runCts?.Cancel();
            _deadline = null;
            _kinds = Trigger.None;
            _hint = null;
            _immediate = _redo = false;
            _lastKinds = Trigger.None;
            _lastHint = null;
            _lastReply = "";
            _lastScreenText = null;
            _rejected.Clear();
        }
        Conversation.Clear();
    }

    // -- worker -----------------------------------------------------------------------------

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Trigger kinds = Trigger.None;
            string? hint = null;
            bool redo = false;
            string[] rejected = Array.Empty<string>();
            CancellationToken runToken = default;
            int epoch = 0;
            TimeSpan wait = Timeout.InfiniteTimeSpan;
            bool run = false;

            lock (_gate)
            {
                if (_deadline is { } deadline)
                {
                    double remaining = deadline - Now;
                    if (remaining > 0)
                        wait = TimeSpan.FromSeconds(remaining);
                    else if (!_immediate && _watcher is { Pending: true } && Now - _queuedAt < Options.MergeHold.TotalSeconds)
                        wait = TimeSpan.FromMilliseconds(200); // let the watched text settle so SAY and TYPE arrive together
                    else
                    {
                        kinds = _kinds; hint = _hint; redo = _redo;
                        _kinds = Trigger.None; _hint = null; _deadline = null;
                        _immediate = _redo = false;
                        if (!redo) _rejected.Clear(); // a new moment: nothing here was thrown away yet
                        rejected = _rejected.ToArray();
                        _lastKinds = kinds; _lastHint = hint; _lastReply = "";
                        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        runToken = _runCts.Token;
                        epoch = _clearEpoch;
                        run = true;
                    }
                }
            }

            if (run)
            {
                try { await RunAsync(kinds, hint, redo, rejected, runToken, epoch); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Whatever went wrong, the engine must keep serving later requests.
                    AppLog.Error("Suggestion run failed", ex);
                    Emit(new EngineEvent(EngineEventKind.Error, SuggesterErrors.Describe(ex)));
                    Emit(new EngineEvent(EngineEventKind.SuggestEnd));
                }
            }
            else
            {
                try { await _signal.WaitAsync(wait, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunAsync(Trigger kinds, string? hint, bool redo, string[] rejected, CancellationToken ct, int epoch)
    {
        bool started = false;
        void Start()
        {
            if (started) return;
            started = true;
            Emit(new EngineEvent(EngineEventKind.SuggestStart, redo ? "Trying another take…"
                : kinds.HasFlag(Trigger.Forced) ? "Reading the text area…" : null));
        }

        var settings = Settings.Normalized();
        byte[]? png = null;
        string? screenText = null;
        if (Region is { } region && TextEnabled && (kinds & ~(Trigger.Speech | Trigger.Pause)) != Trigger.None)
        {
            // Spoken-only triggers skip the screen; any other trigger includes what's on screen now.
            bool readAsText = ReadsScreenAsText(settings);
            // Only the watched text changed: if its words are the same as the last time, there is nothing new to answer.
            bool mayRepeat = !redo && (kinds & ~Trigger.Text) == Trigger.None;
            if (!(readAsText && mayRepeat)) Start();
            if (readAsText)
            {
                screenText = await ReadScreenTextAsync(region, ct);
                if (screenText is not null && mayRepeat && SameWords(screenText, _lastScreenText)) return;
                Start();
                if (screenText is not null) lock (_gate) _lastScreenText = screenText;
            }
            if (screenText is null)
            {
                try { png = _capture.CapturePng(region); }
                catch (Exception ex)
                {
                    Emit(new EngineEvent(EngineEventKind.Status, $"Couldn't capture the text area ({ex.Message}); continuing without it."));
                }
            }
        }
        Start();

        var request = new SuggestionRequest(
            Conversation.Window(Options.TranscriptChars, Options.TranscriptKeepChars), settings, kinds, hint, png,
            rejected.Length > 0 ? rejected : null)
        {
            RegionText = screenText,
            OnUsage = u =>
            {
                Usage.Add(u);
                Emit(new EngineEvent(EngineEventKind.Usage));
            },
        };
        var reply = new System.Text.StringBuilder();
        bool failed = false;
        int seq = Volatile.Read(ref _turnSeq);
        try
        {
            await foreach (var chunk in _suggester.StreamAsync(request, ct))
            {
                ct.ThrowIfCancellationRequested();
                reply.Append(chunk);
                lock (_gate)
                {
                    // Checked under the lock Regenerate cancels in, so a late chunk can't undo its reset.
                    if (!ct.IsCancellationRequested) _lastReply = reply.ToString();
                }
                Emit(new EngineEvent(EngineEventKind.Chunk, chunk));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer trigger: hand what this run owed to the one that replaced it.
            lock (_gate)
            {
                if (!_stop.IsCancellationRequested && epoch == _clearEpoch)
                {
                    _kinds |= kinds;
                    _hint ??= hint;
                }
            }
            return;
        }
        catch (Exception ex)
        {
            failed = true;
            Emit(new EngineEvent(EngineEventKind.Error, SuggesterErrors.Describe(ex)));
        }
        Emit(new EngineEvent(EngineEventKind.SuggestEnd));

        // Spoken words that Claude saw nothing to answer: if the quiet goes on, look again with that in mind.
        if (!failed && kinds.HasFlag(Trigger.Speech) && !kinds.HasFlag(Trigger.Pause)
            && reply.ToString().TrimStart().StartsWith("(nothing", StringComparison.OrdinalIgnoreCase))
            ScheduleFollowUp(seq, epoch);
    }

    /// <summary>Fast screen reading is chosen (or the model can't read pictures) and this PC can read text from the screen.</summary>
    private bool ReadsScreenAsText(Settings settings)
    {
        if (_textReader is null) return false;
        bool wanted = settings.ScreenReading == ScreenReading.Fast || !Providers.CanSeePictures(settings.Provider, settings.Model);
        if (!wanted) return false;
        if (_textReader.IsAvailable) return true;
        if (!_warnedNoReader)
        {
            _warnedNoReader = true;
            Emit(new EngineEvent(EngineEventKind.Status,
                "This PC can't read text from the screen (Windows has no text recognition installed for your language), so the text area is sent as a picture."));
        }
        return false;
    }

    /// <summary>The words in the watched region, read on this PC; null if they can't be read, in which case a picture is sent instead.</summary>
    private async Task<string?> ReadScreenTextAsync(Region region, CancellationToken ct)
    {
        try
        {
            var (bgra, width, height) = _capture.GrabBgra(region);
            var text = (await _textReader!.ReadAsync(bgra, width, height, ct)).Trim();
            if (text.Count(char.IsLetterOrDigit) >= 2) return text;
            Emit(new EngineEvent(EngineEventKind.Status, "No words could be read in the text area, so it is sent as a picture."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the text area failed, sending a picture instead: {ex.Message}");
            Emit(new EngineEvent(EngineEventKind.Status, $"Couldn't read the words in the text area ({ex.Message}), so it is sent as a picture."));
        }
        return null;
    }

    private static bool SameWords(string a, string? b) =>
        b is not null && string.Equals(string.Join(' ', a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                                       string.Join(' ', b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.Ordinal);

    private void ScheduleFollowUp(int seq, int epoch)
    {
        var wait = Options.PauseFollowUp;
        if (wait <= TimeSpan.Zero || wait == Timeout.InfiniteTimeSpan) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(wait, _stop.Token); }
            catch (OperationCanceledException) { return; }
            lock (_gate)
            {
                // Only if the quiet really went on: nothing was said or cleared since, nobody is speaking now,
                // recording is still on, and no other request is already waiting.
                if (Paused || epoch != _clearEpoch || seq != Volatile.Read(ref _turnSeq) || _speaking.Count > 0 || _deadline is not null) return;
            }
            Request(Trigger.Pause);
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _signal.Release();
        lock (_watcherLock) _watcher?.Dispose();
    }
}
