using Assistant.App.Platform;
using Assistant.App.ViewModels;
using Assistant.App.Views;
using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Assistant.App;

/// <summary>Owns the long-lived pieces (settings, key, engine, audio, speech model) and the windows.</summary>
public sealed class AppHost : IDisposable
{
    private readonly ApiKeyStore _keys = new();
    private readonly SettingsStore _store = new();
    private readonly SwappableSpeechToText _speech = new();
    private readonly ClaudeSuggester _suggester;
    private readonly RegionOutline _outline = new();
    private readonly Timer _saveTimer;
    private string? _apiKey;
    private AudioPipeline? _pipeline;
    private IAudioSource? _mic;
    private CancellationTokenSource? _modelCts;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private SpeechAccuracy _loadedAccuracy;
    private bool _speechStarted;
    private Action<EngineEvent>? _regionHandler;
    private bool _hiddenFromCapture;

    public AppHost()
    {
        Settings = _store.Exists ? _store.Load() : Settings.ForFirstRun(Environment.ProcessorCount);
        _apiKey = _keys.Load();
        Capture = PlatformServices.CreateScreenCapture();
        _suggester = new ClaudeSuggester(() => _apiKey);
        Engine = new Engine(new EngineOptions { AutoSuggest = Settings.AutoSuggest }, _suggester, Capture)
        {
            Settings = Settings,
        };
        Engine.SetTextEnabled(Settings.TypeEnabled);

        // Windows are flagged as they open, so this has to be decided before the first one is shown.
        if (Settings.HideFromCapture && !CaptureShield.SetHidden(true)) Settings.HideFromCapture = false;
        _hiddenFromCapture = Settings.HideFromCapture;
        _saveTimer = new Timer(_ => _store.Save(Settings), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Settings Settings { get; }
    public Engine Engine { get; }
    public IScreenCapture Capture { get; }
    public bool HasKey => _apiKey is not null;

    /// <summary>Raised after the user removes their key in Settings.</summary>
    public event Action? KeyRemoved;

    public void SetKey(string? key)
    {
        if (key is null)
        {
            _keys.Delete();
            _apiKey = null;
            StopSession();
            KeyRemoved?.Invoke();
            return;
        }
        _apiKey = key;
        try { _keys.Save(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Keep going with the key for this session; just say it won't be remembered.
            AppLog.Error("Couldn't save the API key", ex);
            _vm?.SetProblem("Couldn't save your key on this PC, so you'll be asked for it again next time.");
        }
    }

    public MainWindow CreateMainWindow()
    {
        _vm?.Dispose();
        _vm = new MainViewModel(Engine, Settings, CopyAsync, PickRegionAsync, OpenSettings, ApplySettings,
            () => LoadSpeech(force: true), SetRecording);
        _window = new MainWindow { DataContext = _vm };
        _window.Opened += (_, _) =>
        {
            WindowFit.ClampToScreen(_window);
            PlaceTopRight(_window);
            Engine.Start();
            StartListening();
        };
        if (_regionHandler is not null) Engine.Event -= _regionHandler;
        _regionHandler = e =>
        {
            if (e.Kind is EngineEventKind.RegionChanged or EngineEventKind.ModeChanged)
                Dispatcher.UIThread.Post(() => ShowOutline(Engine.IsWatching ? Engine.Region : null));
        };
        Engine.Event += _regionHandler;
        return _window;
    }

    private static void PlaceTopRight(Window w)
    {
        var area = w.Screens.Primary?.WorkingArea;
        if (area is not { } a) return;
        double scale = w.Screens.Primary!.Scaling;
        w.Position = new PixelPoint(Math.Max(a.X, a.Right - (int)(w.Width * scale) - 24), a.Y + 32);
        WindowFit.KeepOnScreen(w);
    }

    /// <summary>Called for an unexpected exception on the UI thread: say so, keep running.</summary>
    public void ReportUnexpected(Exception ex) =>
        _vm?.SetProblem($"Something unexpected went wrong ({ex.GetType().Name}). The app is still running; details are in the log.");

    // -- listening ------------------------------------------------------------------------------

    private void StartListening()
    {
        StartAudio();
        LoadSpeech(force: false);
    }

    private void StartAudio()
    {
        if (_pipeline is not null) return;
        _pipeline = new AudioPipeline(Engine, Task.FromResult<ISpeechToText>(_speech));
        if (!OperatingSystem.IsWindows())
        {
            _vm?.SetProblem("Listening to your speakers is only available on Windows in this version.");
            return;
        }
        _pipeline.Add(Speaker.Them, new WasapiAudioSource(loopback: true));
        _pipeline.Start();
        if (Settings.UseMicrophone) AttachMic();
    }

    /// <summary>Release the audio devices (so Windows' own "microphone in use" indicator goes off too).</summary>
    private void StopAudio()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        _mic = null;
    }

    /// <summary>The Recording switch: off stops listening altogether, on starts again.</summary>
    private void SetRecording(bool on)
    {
        if (on) StartAudio(); else StopAudio();
    }

    /// <summary>Stop listening and watching (used when the key is removed and the welcome screen returns).</summary>
    private void StopSession()
    {
        _modelCts?.Cancel();
        StopAudio();
        _speechStarted = false;
        Engine.SetRegion(null);
        Engine.ClearConversation();
        _outline.Hide();
    }

    private void AttachMic()
    {
        if (_mic is not null || _pipeline is null || !OperatingSystem.IsWindows()) return;
        _mic = new WasapiAudioSource(loopback: false);
        _pipeline.Add(Speaker.Me, _mic);
    }

    private void DetachMic()
    {
        if (_mic is null || _pipeline is null) return;
        _pipeline.Remove(_mic);
        _mic = null;
    }

    private void LoadSpeech(bool force)
    {
        if (_speechStarted && !force && _loadedAccuracy == Settings.SpeechAccuracy) return;
        _speechStarted = true;
        _loadedAccuracy = Settings.SpeechAccuracy;
        _modelCts?.Cancel();
        var cts = _modelCts = new CancellationTokenSource();
        var accuracy = Settings.SpeechAccuracy;

        void Ui(Action a) => Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) a(); });
        var task = Task.Run<ISpeechToText>(async () =>
        {
            Ui(() => _vm?.SetPreparing(SpeechModel.IsDownloaded(accuracy) ? "Loading speech recognition…" : "Downloading speech recognition (one time)…", SpeechModel.IsDownloaded(accuracy) ? null : 0));
            var path = await SpeechModel.EnsureAsync(accuracy,
                new Progress<double>(p => Ui(() => _vm?.SetPreparing($"Downloading speech recognition… {p:P0}", p))), cts.Token);
            Ui(() => _vm?.SetPreparing("Loading speech recognition…", null));
            WhisperSpeechToText stt;
            try { stt = new WhisperSpeechToText(path); }
            catch (Whisper.net.WhisperModelLoadException ex)
            {
                // The file is there but won't load (damaged): fetch a fresh copy once.
                AppLog.Error("The speech model wouldn't load; downloading it again", ex);
                File.Delete(path);
                path = await SpeechModel.EnsureAsync(accuracy,
                    new Progress<double>(p => Ui(() => _vm?.SetPreparing($"Downloading speech recognition… {p:P0}", p))), cts.Token);
                stt = new WhisperSpeechToText(path);
            }
            AppLog.Info($"Speech model ready ({accuracy}); native runtime: {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary}");
            Ui(() => _vm?.SetListening());
            return stt;
        }, cts.Token);
        _speech.Set(task);
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && !cts.IsCancellationRequested)
            {
                var ex = t.Exception!.GetBaseException();
                AppLog.Error("Speech recognition setup failed", ex);
                Ui(() => _vm?.SetProblem(SpeechModel.Explain(ex, accuracy), canRetry: true));
            }
        }, TaskScheduler.Default);
    }

    // -- settings ---------------------------------------------------------------------------------

    private void ApplySettings()
    {
        Engine.Settings = Settings.Normalized();
        Engine.Options.AutoSuggest = Settings.AutoSuggest;
        if (Settings.UseMicrophone) AttachMic(); else DetachMic();
        if (Settings.HideFromCapture != _hiddenFromCapture)
        {
            if (CaptureShield.SetHidden(Settings.HideFromCapture)) _hiddenFromCapture = Settings.HideFromCapture;
            else
            {
                Settings.HideFromCapture = _hiddenFromCapture;
                _vm?.SetProblem("Couldn't hide the windows from screen capture on this PC.");
            }
        }
        if (_speechStarted && _loadedAccuracy != Settings.SpeechAccuracy) LoadSpeech(force: true);
        _saveTimer.Change(400, Timeout.Infinite); // debounce: typing in a field changes settings per keystroke
    }

    private SettingsWindow? _settingsWindow;
    private bool _picking;

    private void OpenSettings()
    {
        if (_window is null) return;
        if (_settingsWindow is { } open) { open.Activate(); return; } // already open: don't stack a second one
        var entry = new KeyEntryViewModel();
        var vm = new SettingsViewModel(Settings, ApplySettings, entry, _apiKey, key =>
        {
            SetKey(key);
        });
        var win = new SettingsWindow { DataContext = vm };
        KeyRemoved += CloseOnRemoved;
        _settingsWindow = win;
        win.Closed += (_, _) => { KeyRemoved -= CloseOnRemoved; _settingsWindow = null; };
        void CloseOnRemoved() => win.Close();
        win.Show(_window);
    }

    // -- text area --------------------------------------------------------------------------------

    private async Task PickRegionAsync()
    {
        if (_window is null || _picking) return; // a second click while the picker is open does nothing
        _picking = true;
        try
        {
            var region = await RegionPicker.PickAsync(_window, Capture);
            if (region is { } r) Engine.SetRegion(r);
        }
        catch (Exception ex)
        {
            AppLog.Error("The area picker failed", ex);
            _vm?.SetProblem($"Couldn't capture the screen: {ex.Message}");
        }
        finally { _picking = false; }
    }

    private void ShowOutline(Region? region)
    {
        if (region is { } r && _window is not null) _outline.Show(_window, r);
        else _outline.Hide();
    }

    private async Task CopyAsync(string text)
    {
        if (_window is null) return;
        var clipboard = TopLevel.GetTopLevel(_window)?.Clipboard;
        if (clipboard is null) return;
        // Another program holding the clipboard open makes this fail for a moment: try a few times.
        for (int attempt = 1; ; attempt++)
        {
            try { await clipboard.SetTextAsync(text); return; }
            catch (Exception ex) when (attempt < 4)
            {
                AppLog.Warn($"Clipboard busy ({ex.GetType().Name}); retrying");
                await Task.Delay(120);
            }
            catch (Exception ex)
            {
                AppLog.Error("Couldn't copy to the clipboard", ex);
                _vm?.SetProblem("Couldn't copy: another program is using the clipboard. Try again.");
                return;
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        _store.Save(Settings);
        _modelCts?.Cancel();
        _outline.Dispose();
        _pipeline?.Dispose();
        Engine.Dispose();
        _speech.Dispose();
    }
}
