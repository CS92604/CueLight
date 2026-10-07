using System.Diagnostics;

namespace Assistant.Core;

public enum EngineEventKind { Turn, SuggestStart, Chunk, SuggestEnd, Status, Error, RegionChanged }

public sealed record EngineEvent(
    EngineEventKind Kind, string? Text = null, Speaker? Speaker = null, Region? Region = null);

public sealed class EngineOptions
{
    public bool AutoSuggest { get; set; } = true;
    /// <summary>Ignore short backchannels ("mm-hmm", "okay") for automatic suggestions.</summary>
    public int AutoMinWords { get; set; } = 4;
    /// <summary>Wait this long after the last trigger before asking Claude.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Max time to hold a spoken request while the watched text is still changing.</summary>
    public TimeSpan MergeHold { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Safety cap on the transcript sent with each request. Claude gets the whole conversation, so
    /// this is large (about two hours of speech); only a longer call drops its oldest turns.
    /// </summary>
    public int TranscriptChars { get; set; } = 120_000;
}

/// <summary>
/// Holds the conversation and turns it into streamed Claude suggestions.
///
/// Two things can trigger a suggestion: new speech from "Them", and the watched screen region
/// changing. Requests are coalesced: a newer trigger supersedes one still waiting or streaming,
/// and the channels that triggered are merged, so if speech and on-screen text change together
/// Claude is asked for both a SAY and a TYPE reply.
///
/// Each request carries the full transcript but only the current picture of the watched region;
/// earlier pictures are never kept or resent. <see cref="Panic"/> forces an immediate re-read of
/// the region, and <see cref="Regenerate"/> redoes the last reply with a different take.
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly ISuggester _suggester;
    private readonly IScreenCapture _capture;
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
    private readonly List<string> _rejected = new();   // replies the user has regenerated away
    private double? _deadline;      // seconds on _clock
    private double _queuedAt;
    private CancellationTokenSource? _runCts;
    private int _clearEpoch;
    private IScreenWatcher? _watcher;
    private Task? _loop;

    public Engine(EngineOptions options, ISuggester suggester, IScreenCapture capture,
        Func<Region, Action, Action<string>, IScreenWatcher>? watcherFactory = null)
    {
        Options = options;
        _suggester = suggester;
        _capture = capture;
        _watcherFactory = watcherFactory ?? ((r, onChange, onError) => new ScreenWatcher(r, capture, onChange, onError));
        Settings = new Settings();
    }

    public EngineOptions Options { get; }
    public Settings Settings { get; set; }
    public Conversation Conversation { get; } = new();
    public Region? Region { get; private set; }

    /// <summary>Raised from background threads; marshal to the UI thread before touching controls.</summary>
    public event Action<EngineEvent>? Event;

    private double Now => _clock.Elapsed.TotalSeconds;
    private void Emit(EngineEvent e) => Event?.Invoke(e);

    public void Start() => _loop ??= Task.Run(LoopAsync);

    // -- watched region ---------------------------------------------------------------------

    public void SetRegion(Region? region)
    {
        IScreenWatcher? old;
        lock (_gate) { old = _watcher; _watcher = null; Region = region; }
        old?.Dispose();
        Emit(new EngineEvent(EngineEventKind.RegionChanged, Region: region));
        if (region is { } r)
        {
            var watcher = _watcherFactory(r, OnScreenChanged, msg => Emit(new EngineEvent(EngineEventKind.Error, msg)));
            lock (_gate) _watcher = watcher;
            watcher.Start();
        }
    }

    /// <summary>Surface a problem from a background component (audio device, speech model, ...).</summary>
    public void RaiseError(string message) => Emit(new EngineEvent(EngineEventKind.Error, message));

    public void OnScreenChanged()
    {
        if (Options.AutoSuggest && Region is not null) Request(Trigger.Text, delay: Options.Debounce);
    }

    // -- inputs -----------------------------------------------------------------------------

    public void AddTurn(Speaker speaker, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        Conversation.Add(speaker, text);
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

            if (run) await RunAsync(kinds, hint, redo, rejected, runToken, epoch);
            else
            {
                try { await _signal.WaitAsync(wait, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunAsync(Trigger kinds, string? hint, bool redo, string[] rejected, CancellationToken ct, int epoch)
    {
        Emit(new EngineEvent(EngineEventKind.SuggestStart, redo ? "Trying another take…"
            : kinds.HasFlag(Trigger.Forced) ? "Reading the text area…" : null));
        byte[]? png = null;
        if (Region is { } region && kinds != Trigger.Speech)
        {
            // Spoken-only triggers skip the image; any other trigger includes what's on screen now.
            try { png = _capture.CapturePng(region); }
            catch (Exception ex)
            {
                Emit(new EngineEvent(EngineEventKind.Status, $"Couldn't capture the text area ({ex.Message}); continuing without it."));
            }
        }

        var request = new SuggestionRequest(
            Conversation.Render(Options.TranscriptChars), Settings.Normalized(), kinds, hint, png,
            rejected.Length > 0 ? rejected : null);
        var reply = new System.Text.StringBuilder();
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
            Emit(new EngineEvent(EngineEventKind.Error, ClaudeSuggester.Describe(ex)));
        }
        Emit(new EngineEvent(EngineEventKind.SuggestEnd));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _signal.Release();
        _watcher?.Dispose();
    }
}
