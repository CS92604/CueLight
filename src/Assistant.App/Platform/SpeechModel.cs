using System.Net;
using System.Net.Http.Headers;
using Assistant.Core;
using Whisper.net;

namespace Assistant.App.Platform;

/// <summary>Finds, downloads (once) and loads the local Whisper speech model.</summary>
public static class SpeechModel
{
    public const string DefaultBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    // Every ggml model file starts with this magic number ("ggml", little-endian).
    private static readonly byte[] Magic = { 0x6C, 0x6D, 0x67, 0x67 };

    public static (string File, long ApproxBytes) Describe(SpeechAccuracy accuracy) => accuracy switch
    {
        SpeechAccuracy.Fast => ("ggml-tiny.en.bin", 75_000_000),
        SpeechAccuracy.Accurate => ("ggml-small.en.bin", 466_000_000),
        _ => ("ggml-base.en.bin", 142_000_000),
    };

    /// <summary>Lets tests keep models out of the real data folder.</summary>
    internal static string? FolderOverride { get; set; }

    public static string Folder => FolderOverride ?? Path.Combine(AppPaths.DataDirectory, "models");

    public static string PathFor(SpeechAccuracy accuracy) => Path.Combine(Folder, Describe(accuracy).File);

    public static bool IsDownloaded(SpeechAccuracy accuracy) => File.Exists(PathFor(accuracy));

    /// <summary>What to do when the automatic download can't work (a blocked network, say).</summary>
    public static string ManualHelp(SpeechAccuracy accuracy) =>
        $"To set it up by hand, download {Describe(accuracy).File} from huggingface.co/ggerganov/whisper.cpp on another connection and put it in {Folder}.";

    /// <summary>A plain-language reason (and what to do) for a failure while preparing the speech model.</summary>
    public static string Explain(Exception ex, SpeechAccuracy accuracy)
    {
        var (file, approx) = Describe(accuracy);
        return ex switch
        {
            OperationCanceledException => "Setting up speech recognition was interrupted.",
            HttpRequestException or System.Net.Sockets.SocketException =>
                $"Couldn't download the speech model: it needs one connection to huggingface.co. Check your internet, proxy or firewall and press Retry. {ManualHelp(accuracy)}",
            UnauthorizedAccessException =>
                $"Windows wouldn't let the app save the speech model in {Folder}. Free up that folder or run the app from another user account.",
            DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or FileNotFoundException { FileName: null or "" } =>
                $"The speech engine couldn't start. Security software may have blocked or removed its files in {SpeechRuntime.Folder}: allow that folder (or the app) and press Retry. "
                + "If that doesn't help, install the \"Microsoft Visual C++ Redistributable (x64)\" from microsoft.com.",
            IOException io when (io.HResult & 0xFFFF) == 112 =>
                $"There isn't enough free disk space for the speech model (about {approx / 1_000_000} MB). Free some space and press Retry.",
            IOException io when io.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase) => io.Message,
            IOException io => $"Couldn't save the speech model: {io.Message} Press Retry. {ManualHelp(accuracy)}",
            OutOfMemoryException => "This PC doesn't have enough free memory for that speech model. Choose Fast in Settings.",
            _ => $"Couldn't set up speech recognition: {ex.Message}",
        };
    }

    /// <summary>True if the file looks like a complete ggml model (right header, plausible size).</summary>
    public static bool LooksValid(string path, long approxBytes)
    {
        try
        {
            using var f = File.OpenRead(path);
            if (f.Length < approxBytes / 2) return false;
            var head = new byte[4];
            return f.Read(head, 0, 4) == 4 && head.AsSpan().SequenceEqual(Magic);
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Downloads the model if it isn't on disk yet. Progress is 0..1. An interrupted download
    /// resumes where it stopped. The finished file is checked (full length, ggml header) before it
    /// is put in place, so a dropped connection or an error page can't leave a broken model behind.
    /// </summary>
    public static async Task<string> EnsureAsync(SpeechAccuracy accuracy, IProgress<double>? progress, CancellationToken ct,
        HttpMessageHandler? handler = null, string? baseUrl = null)
    {
        var path = PathFor(accuracy);
        var (file, approx) = Describe(accuracy);
        if (File.Exists(path))
        {
            if (LooksValid(path, approx)) return path;
            AppLog.Warn($"{file} is damaged; downloading it again.");
            File.Delete(path); // e.g. cut short by an antivirus scan or a full disk
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeLiveAssistant/1.0");

        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        async Task<HttpResponseMessage> Send(long from)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, (baseUrl ?? DefaultBaseUrl) + file);
            if (from > 0) request.Headers.Range = new RangeHeaderValue(from, null);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        var response = await Send(have);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The leftover part doesn't fit what the server has: start over.
            response.Dispose();
            File.Delete(partial);
            have = 0;
            response = await Send(0);
        }
        using var held = response;
        response.EnsureSuccessStatusCode();
        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) have = 0; // the server sent the whole file, so write it from the top

        long? remaining = response.Content.Headers.ContentLength;
        long total = remaining is { } r ? have + r : approx;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[128 * 1024];
            long done = have;
            double reported = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                double p = Math.Min(1.0, done / (double)total);
                if (p - reported >= 0.01) { reported = p; progress?.Report(p); }
            }
        }

        long size = new FileInfo(partial).Length;
        if ((remaining is not null && size != total) || (remaining is null && size < approx / 2) || !LooksValid(partial, 0))
        {
            // Keep a plain truncation (to resume later); throw away a file that isn't a model at all.
            if (!LooksValid(partial, 0) && size > 0) File.Delete(partial);
            throw new IOException("The speech model download was incomplete. Check your connection and try again.");
        }
        File.Move(partial, path, overwrite: true);
        progress?.Report(1.0);
        return path;
    }
}

/// <summary>Whisper (whisper.cpp) speech recognition, running locally on the CPU.</summary>
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _one = new(1, 1);
    private byte[]? _modelBytes;   // only when the model had to be read from memory
    private int _disposed;

    public WhisperSpeechToText(string modelPath)
    {
        SpeechRuntime.Prepare(); // unpack the engine's libraries from inside the .exe the first time
        try
        {
            (_factory, _processor) = Build(() => WhisperFactory.FromPath(modelPath));
        }
        catch (Exception ex) when (modelPath.Any(c => c > 127))
        {
            // A user name such as "José" or "Иван" puts non-ASCII letters in the path, which some builds
            // of the native library can't open. Hand it the file's bytes instead.
            AppLog.Warn($"Loading the model by path failed ({ex.Message}); loading it from memory.");
            _modelBytes = File.ReadAllBytes(modelPath);
            (_factory, _processor) = Build(() => WhisperFactory.FromBuffer(_modelBytes));
        }
    }

    private static (WhisperFactory, WhisperProcessor) Build(Func<WhisperFactory> open)
    {
        var factory = open();
        try
        {
            var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .Build();
            return (factory, processor);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return "";
            var parts = new List<string>();
            await foreach (var segment in _processor.ProcessAsync(audio16k, ct))
                parts.Add(segment.Text.Trim());
            return string.Join(" ", parts.Where(p => p.Length > 0));
        }
        finally { _one.Release(); }
    }

    /// <summary>Frees the model once any transcription in progress is done: freeing it underneath
    /// one would crash the whole process. Returns immediately.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await _one.WaitAsync();
            try
            {
                _processor.Dispose();
                _factory.Dispose();
                _modelBytes = null;
            }
            catch (Exception ex) { AppLog.Error("Freeing the speech model", ex); }
            finally { _one.Release(); }
        });
    }
}

/// <summary>Lets the speech model be replaced (for example when the accuracy setting changes)
/// without rebuilding the pipeline. Calls wait until a model is ready.</summary>
public sealed class SwappableSpeechToText : ISpeechToText, IDisposable
{
    private volatile Task<ISpeechToText> _current = new TaskCompletionSource<ISpeechToText>().Task;

    public void Set(Task<ISpeechToText> next)
    {
        var old = _current;
        _current = next;
        DisposeWhenReady(old);
    }

    // A model still loading when it is replaced would otherwise never be freed.
    private static void DisposeWhenReady(Task<ISpeechToText> model) =>
        model.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is IDisposable d) d.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct)
    {
        var model = _current;
        try
        {
            return await (await model.WaitAsync(ct)).TranscribeAsync(audio16k, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ""; // the model was swapped while this utterance waited; it is stale anyway
        }
        catch when (model.IsFaulted)
        {
            return ""; // loading failed: the host already told the user once, with a Retry button
        }
    }

    public void Dispose() => DisposeWhenReady(_current);
}
