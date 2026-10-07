using Assistant.Core;
using Whisper.net;

namespace Assistant.App.Platform;

/// <summary>Finds, downloads (once) and loads the local Whisper speech model.</summary>
public static class SpeechModel
{
    public static (string File, long ApproxBytes) Describe(SpeechAccuracy accuracy) => accuracy switch
    {
        SpeechAccuracy.Fast => ("ggml-tiny.en.bin", 75_000_000),
        SpeechAccuracy.Accurate => ("ggml-small.en.bin", 466_000_000),
        _ => ("ggml-base.en.bin", 142_000_000),
    };

    public static string PathFor(SpeechAccuracy accuracy) =>
        Path.Combine(AppPaths.DataDirectory, "models", Describe(accuracy).File);

    public static bool IsDownloaded(SpeechAccuracy accuracy) => File.Exists(PathFor(accuracy));

    /// <summary>Downloads the model if it isn't on disk yet. Progress is 0..1.</summary>
    public static async Task<string> EnsureAsync(SpeechAccuracy accuracy, IProgress<double>? progress, CancellationToken ct)
    {
        var path = PathFor(accuracy);
        if (File.Exists(path)) return path;

        var (file, approx) = Describe(accuracy);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeLiveAssistant/1.0");
        using var response = await http.GetAsync(
            $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{file}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? approx;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(Math.Min(1.0, done / (double)total));
            }
        }

        if (new FileInfo(partial).Length < approx / 2)
        {
            File.Delete(partial);
            throw new IOException("The speech model download was incomplete.");
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

    public WhisperSpeechToText(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();
    }

    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var parts = new List<string>();
            await foreach (var segment in _processor.ProcessAsync(audio16k, ct))
                parts.Add(segment.Text.Trim());
            return string.Join(" ", parts.Where(p => p.Length > 0));
        }
        finally { _one.Release(); }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
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
        if (old.IsCompletedSuccessfully && old.Result is IDisposable d) d.Dispose();
    }

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

    public void Dispose()
    {
        if (_current.IsCompletedSuccessfully && _current.Result is IDisposable d) d.Dispose();
    }
}
