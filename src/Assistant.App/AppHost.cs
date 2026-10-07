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

    public AppHost()
    {
        Settings = _store.Load();
        _apiKey = _keys.Load();
        Capture = PlatformServices.CreateScreenCapture();
        _suggester = new ClaudeSuggester(() => _apiKey);
        Engine = new Engine(new EngineOptions { AutoSuggest = Settings.AutoSuggest }, _suggester, Capture)
        {
            Settings = Settings,
        };
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
        _keys.Save(key);
        _apiKey = key;
    }

    public MainWindow CreateMainWindow()
    {
        _vm?.Dispose();
        _vm = new MainViewModel(Engine, Settings, CopyAsync, PickRegionAsync, OpenSettings, ApplySettings, () => LoadSpeech(force: true));
        _window = new MainWindow { DataContext = _vm };
        _window.Opened += (_, _) =>
        {
            PlaceTopRight(_window);
            Engine.Start();
            StartListening();
        };
        if (_regionHandler is not null) Engine.Event -= _regionHandler;
        _regionHandler = e =>
        {
            if (e.Kind == EngineEventKind.RegionChanged)
                Dispatcher.UIThread.Post(() => ShowOutline(e.Region));
        };
        Engine.Event += _regionHandler;
        return _window;
    }

    private static void PlaceTopRight(Window w)
    {
        var area = w.Screens.Primary?.WorkingArea;
        if (area is not { } a) return;
        double scale = w.Screens.Primary!.Scaling;
        w.Position = new PixelPoint(a.Right - (int)(w.Width * scale) - 24, a.Y + 32);
    }

    // -- listening ------------------------------------------------------------------------------

    private void StartListening()
    {
        _pipeline = new AudioPipeline(Engine, Task.FromResult<ISpeechToText>(_speech));
        if (!OperatingSystem.IsWindows())
        {
            _vm?.SetProblem("Listening to your speakers is only available on Windows in this version.");
            return;
        }
        _pipeline.Add(Speaker.Them, new WasapiAudioSource(loopback: true));
        _pipeline.Start();
        if (Settings.UseMicrophone) AttachMic();
        LoadSpeech(force: false);
    }

    /// <summary>Stop listening and watching (used when the key is removed and the welcome screen returns).</summary>
    private void StopSession()
    {
        _modelCts?.Cancel();
        _pipeline?.Dispose();
        _pipeline = null;
        _mic = null;
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
            var stt = new WhisperSpeechToText(path);
            Ui(() => _vm?.SetListening());
            return stt;
        }, cts.Token);
        _speech.Set(task);
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && !cts.IsCancellationRequested)
                Ui(() => _vm?.SetProblem($"Couldn't set up speech recognition: {t.Exception!.GetBaseException().Message}", canRetry: true));
        }, TaskScheduler.Default);
    }

    // -- settings ---------------------------------------------------------------------------------

    private void ApplySettings()
    {
        Engine.Settings = Settings.Normalized();
        Engine.Options.AutoSuggest = Settings.AutoSuggest;
        if (Settings.UseMicrophone) AttachMic(); else DetachMic();
        if (_speechStarted && _loadedAccuracy != Settings.SpeechAccuracy) LoadSpeech(force: true);
        _saveTimer.Change(400, Timeout.Infinite); // debounce: typing in a field changes settings per keystroke
    }

    private void OpenSettings()
    {
        if (_window is null) return;
        var entry = new KeyEntryViewModel();
        var vm = new SettingsViewModel(Settings, ApplySettings, entry, _apiKey, key =>
        {
            SetKey(key);
        });
        var win = new SettingsWindow { DataContext = vm };
        KeyRemoved += CloseOnRemoved;
        win.Closed += (_, _) => KeyRemoved -= CloseOnRemoved;
        void CloseOnRemoved() => win.Close();
        win.Show(_window);
    }

    // -- text area --------------------------------------------------------------------------------

    private async Task PickRegionAsync()
    {
        if (_window is null) return;
        try
        {
            var region = await RegionPicker.PickAsync(_window, Capture);
            if (region is { } r) Engine.SetRegion(r);
        }
        catch (Exception ex)
        {
            _vm?.SetProblem($"Couldn't capture the screen: {ex.Message}");
        }
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
        if (clipboard is not null) await clipboard.SetTextAsync(text);
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
