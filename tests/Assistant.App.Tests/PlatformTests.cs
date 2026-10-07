using System.Net;
using System.Net.Http.Headers;
using Assistant.App.Platform;
using Assistant.Core;

namespace Assistant.App.Tests;

sealed class FakeHandler : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

[Collection("SpeechModelFolder")] // these change where models live, so they must not overlap
public class SpeechModelDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cla-model-" + Guid.NewGuid());
    private readonly string? _old = SpeechModel.FolderOverride;
    private static readonly byte[] Magic = { 0x6C, 0x6D, 0x67, 0x67 };

    public SpeechModelDownloadTests() => SpeechModel.FolderOverride = _dir;
    public void Dispose() { SpeechModel.FolderOverride = _old; try { Directory.Delete(_dir, true); } catch { } }

    static byte[] Model(int size) => Magic.Concat(Enumerable.Range(0, size - 4).Select(i => (byte)(i % 251))).ToArray();

    static HttpResponseMessage Ok(byte[] body, int skip = 0, int? declared = null)
    {
        var part = body.AsSpan(skip).ToArray();
        var r = new HttpResponseMessage(skip > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(part) };
        r.Content.Headers.ContentLength = declared ?? part.Length;
        return r;
    }

    static string Target => SpeechModel.PathFor(SpeechAccuracy.Fast);

    [Fact]
    public async Task Downloads_a_valid_model_into_place_and_reports_progress()
    {
        var model = Model(300_000);
        var handler = new FakeHandler { Respond = _ => Ok(model) };
        var progress = new List<double>();
        var path = await SpeechModel.EnsureAsync(SpeechAccuracy.Fast, new Progress<double>(progress.Add), CancellationToken.None, handler, "http://test/");
        await Task.Delay(50);
        Assert.Equal(Target, path);
        Assert.Equal(model, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal("http://test/ggml-tiny.en.bin", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(1.0, progress[^1]);
        Assert.Equal(progress.OrderBy(x => x), progress); // never goes backwards
    }

    [Fact]
    public async Task A_cut_off_download_fails_clearly_then_resumes_where_it_stopped()
    {
        var model = Model(300_000);
        var handler = new FakeHandler { Respond = _ => Ok(model[..120_000], declared: model.Length) }; // connection dropped
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/"));
        Assert.Contains("incomplete", ex.Message);
        Assert.False(File.Exists(Target));
        Assert.Equal(120_000, new FileInfo(Target + ".part").Length);

        handler.Requests.Clear();
        handler.Respond = req =>
        {
            Assert.Equal(new RangeHeaderValue(120_000, null).ToString(), req.Headers.Range!.ToString());
            return Ok(model, skip: 120_000);
        };
        var path = await SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/");
        Assert.Equal(model, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task An_error_page_is_not_mistaken_for_a_model()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<html>Access denied by your network administrator</html>");
        var handler = new FakeHandler { Respond = _ => Ok(html) };
        await Assert.ThrowsAsync<IOException>(() =>
            SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/"));
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task A_server_error_is_reported_as_one()
    {
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/"));
    }

    [Fact]
    public async Task A_leftover_part_the_server_cant_continue_starts_over()
    {
        var model = Model(200_000);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Target + ".part", Model(50_000));
        var handler = new FakeHandler
        {
            Respond = req => req.Headers.Range is not null ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) : Ok(model),
        };
        var path = await SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/");
        Assert.Equal(model, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_request_still_gives_a_correct_file()
    {
        var model = Model(200_000);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Target + ".part", model[..50_000]);
        var handler = new FakeHandler { Respond = _ => Ok(model) }; // replies 200 with the whole file
        var path = await SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/");
        Assert.Equal(model, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_damaged_model_already_on_disk_is_replaced()
    {
        var model = Model(200_000);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Target, "this is not a model");
        var handler = new FakeHandler { Respond = _ => Ok(model) };
        var path = await SpeechModel.EnsureAsync(SpeechAccuracy.Fast, null, CancellationToken.None, handler, "http://test/");
        Assert.Equal(model, File.ReadAllBytes(path));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Looks_valid_needs_the_header_and_a_plausible_size()
    {
        Directory.CreateDirectory(_dir);
        var good = Path.Combine(_dir, "good.bin");
        File.WriteAllBytes(good, Model(10_000));
        Assert.True(SpeechModel.LooksValid(good, 10_000));
        Assert.False(SpeechModel.LooksValid(good, 1_000_000));
        var bad = Path.Combine(_dir, "bad.bin");
        File.WriteAllBytes(bad, new byte[10_000]);
        Assert.False(SpeechModel.LooksValid(bad, 1_000));
        Assert.False(SpeechModel.LooksValid(Path.Combine(_dir, "missing.bin"), 1_000));
    }
}

[Collection("SpeechModelFolder")]
public class SpeechModelMessageTests
{
    [Fact]
    public void Failures_say_what_to_do()
    {
        var fast = SpeechAccuracy.Fast;
        var net = SpeechModel.Explain(new HttpRequestException("No such host"), fast);
        Assert.Contains("huggingface.co", net);
        Assert.Contains("ggml-tiny.en.bin", net);      // manual instructions name the file...
        Assert.Contains("models", net);                 // ...and the folder

        Assert.Contains("free disk space", SpeechModel.Explain(new IOException("full", unchecked((int)0x80070070)), fast));
        Assert.Contains("Windows wouldn't let", SpeechModel.Explain(new UnauthorizedAccessException(), fast));
        Assert.Contains("Visual C++", SpeechModel.Explain(new DllNotFoundException("whisper"), fast));
        Assert.Contains("memory", SpeechModel.Explain(new OutOfMemoryException(), SpeechAccuracy.Accurate));
        Assert.Contains("incomplete", SpeechModel.Explain(new IOException("The speech model download was incomplete. Check your connection and try again."), fast));
        Assert.Contains("boom", SpeechModel.Explain(new InvalidOperationException("boom"), fast));
    }
}

public class SwappableCleanupTests
{
    sealed class Model : ISpeechToText, IDisposable
    {
        public bool Disposed;
        public Task<string> TranscribeAsync(float[] a, CancellationToken ct) => Task.FromResult("x");
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_model_still_loading_when_replaced_is_freed_when_it_finishes()
    {
        var swap = new SwappableSpeechToText();
        var slow = new TaskCompletionSource<ISpeechToText>();
        var slowModel = new Model();
        swap.Set(slow.Task);
        var replacement = new Model();
        swap.Set(Task.FromResult<ISpeechToText>(replacement));
        Assert.False(slowModel.Disposed);
        slow.SetResult(slowModel);          // the old load finishes after being replaced
        Assert.True(slowModel.Disposed);
        Assert.False(replacement.Disposed);
    }

    [Fact]
    public void Disposing_frees_the_current_model()
    {
        var swap = new SwappableSpeechToText();
        var m = new Model();
        swap.Set(Task.FromResult<ISpeechToText>(m));
        swap.Dispose();
        Assert.True(m.Disposed);
    }
}

public class StartupGuardTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "cla-guard-" + Guid.NewGuid());

    [Fact]
    public void A_start_that_finishes_leaves_the_graphics_card_in_use()
    {
        var dir = Temp();
        var first = new StartupGuard(dir);
        Assert.False(first.UseSoftwareRendering);
        first.Begin();
        first.Succeeded();
        Assert.False(new StartupGuard(dir).UseSoftwareRendering);
    }

    [Fact]
    public void A_start_that_never_finished_switches_to_software_rendering_for_good()
    {
        var dir = Temp();
        var crashed = new StartupGuard(dir);
        crashed.Begin();                                  // ...and the process died before Succeeded()
        var next = new StartupGuard(dir);
        Assert.True(next.UseSoftwareRendering);
        next.Begin();
        next.Succeeded();
        Assert.True(new StartupGuard(dir).UseSoftwareRendering); // stays that way until the flag file is deleted
        File.Delete(Path.Combine(dir, "software-rendering.flag"));
        Assert.False(new StartupGuard(dir).UseSoftwareRendering);
    }
}

public class WindowFitTests
{
    [Theory]
    [InlineData(1920, 1040, 1.0, 1896, 1016)]
    [InlineData(1366, 728, 1.5, 886.67, 461.33)]
    [InlineData(500, 300, 2.0, 300, 300)]   // never smaller than something usable
    public void Limits_leave_a_margin_and_a_floor(int w, int h, double scale, double expectW, double expectH)
    {
        var (maxW, maxH) = WindowFit.Limits(w, h, scale);
        Assert.Equal(expectW, maxW, 1);
        Assert.Equal(expectH, maxH, 1);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void A_second_copy_hands_over_to_the_first()
    {
        if (!OperatingSystem.IsWindows()) return; // named events aren't available elsewhere; the app only runs on Windows
        var name = "test-" + Guid.NewGuid();
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        using var shown = new ManualResetEventSlim();
        first!.Activated += shown.Set;

        Assert.Null(SingleInstance.TryAcquire(name));
        Assert.True(shown.Wait(3000), "the first copy should have been asked to come forward");
    }

    [Fact]
    public void The_lock_is_free_again_once_the_first_copy_exits()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "test-" + Guid.NewGuid();
        SingleInstance.TryAcquire(name)!.Dispose();
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }
}

public class SpeechRuntimeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "speechruntime-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, true); } catch { } }

    private static IReadOnlyDictionary<string, Func<Stream>> Fake(params (string Name, string Text)[] items) =>
        items.ToDictionary(i => i.Name, i => (Func<Stream>)(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(i.Text))));

    [Fact]
    public void The_built_app_carries_both_speech_engines()
    {
        // The .exe has no loose libraries beside it, so these embedded copies are all the speech engine has.
        var names = typeof(SpeechRuntime).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("native/")).ToList();
        foreach (var dll in new[] { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" })
        {
            Assert.Contains("native/win-x64/" + dll, names);
            Assert.Contains("native/noavx/win-x64/" + dll, names);
        }
        Assert.True(SpeechRuntime.IsEmbedded);
    }

    [Fact]
    public void Unpacks_the_real_embedded_libraries_and_is_repeatable()
    {
        var files = SpeechRuntime.Extract(_folder, typeof(SpeechRuntime).Assembly);
        Assert.All(files, f => Assert.True(File.Exists(f), f));
        var whisper = Path.Combine(_folder, "runtimes", "win-x64", "whisper.dll");
        var noAvx = Path.Combine(_folder, "runtimes", "noavx", "win-x64", "whisper.dll");
        Assert.True(File.Exists(whisper));
        Assert.True(File.Exists(noAvx));
        Assert.True(new FileInfo(whisper).Length > 100_000); // the real library, not a stub

        var stamp = File.GetLastWriteTimeUtc(whisper);
        SpeechRuntime.Extract(_folder, typeof(SpeechRuntime).Assembly);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(whisper)); // an intact copy isn't rewritten
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_damaged_copy_is_replaced()
    {
        var resources = Fake(("native/win-x64/whisper.dll", "the real library"));
        SpeechRuntime.Extract(_folder, resources);
        var path = Path.Combine(_folder, "runtimes", "win-x64", "whisper.dll");
        File.WriteAllText(path, "cut sh"); // e.g. a download manager or antivirus cut it short
        SpeechRuntime.Extract(_folder, resources);
        Assert.Equal("the real library", File.ReadAllText(path));
    }

    [Fact]
    public void The_visual_cpp_runtime_goes_next_to_every_engine()
    {
        var files = SpeechRuntime.Extract(_folder, Fake(
            ("native/win-x64/whisper.dll", "a"), ("native/noavx/win-x64/whisper.dll", "b"),
            ("native/vc/msvcp140.dll", "c"), ("native/vc/vcruntime140.dll", "d")));
        foreach (var dir in new[] { "win-x64", Path.Combine("noavx", "win-x64") })
        {
            Assert.True(File.Exists(Path.Combine(_folder, "runtimes", dir, "msvcp140.dll")), dir);
            Assert.True(File.Exists(Path.Combine(_folder, "runtimes", dir, "vcruntime140.dll")), dir);
        }
        Assert.Equal(6, files.Count);
    }

    [Fact]
    public void A_folder_name_with_non_english_letters_works()
    {
        var folder = Path.Combine(_folder, "José Иван");
        SpeechRuntime.Extract(folder, Fake(("native/win-x64/whisper.dll", "x")));
        Assert.True(File.Exists(Path.Combine(folder, "runtimes", "win-x64", "whisper.dll")));
    }

    [Fact]
    public void An_unwritable_place_says_so_instead_of_hiding_it()
    {
        // A file where the folder should be: nothing can be created below it.
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocked");
        File.WriteAllText(blocker, "x");
        Assert.ThrowsAny<IOException>(() => SpeechRuntime.Extract(blocker, Fake(("native/win-x64/whisper.dll", "x"))));
    }

    [Fact]
    public void Missing_engine_files_are_explained_with_where_to_look()
    {
        var text = SpeechModel.Explain(new FileNotFoundException("Native Library not found"), SpeechAccuracy.Fast);
        Assert.Contains("Security software", text);
        Assert.Contains(SpeechRuntime.Folder, text);
    }
}
