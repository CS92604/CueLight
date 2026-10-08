using Assistant.App.Platform;
using Assistant.Core;

namespace Assistant.App.Tests;

public class SwappableSpeechTests
{
    sealed class Echo : ISpeechToText, IDisposable
    {
        public bool Disposed;
        public string Reply = "hello";
        public Task<string> TranscribeAsync(float[] a, CancellationToken ct) => Task.FromResult(Reply);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Waits_for_the_model_then_transcribes()
    {
        var swap = new SwappableSpeechToText();
        var ready = new TaskCompletionSource<ISpeechToText>();
        swap.Set(ready.Task);
        var pending = swap.TranscribeAsync(new float[10], CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        ready.SetResult(new Echo());
        Assert.Equal("hello", await pending);
    }

    [Fact]
    public async Task A_failed_model_load_is_silent_per_utterance()
    {
        var swap = new SwappableSpeechToText();
        swap.Set(Task.FromException<ISpeechToText>(new IOException("no internet")));
        Assert.Equal("", await swap.TranscribeAsync(new float[10], CancellationToken.None));
    }

    [Fact]
    public async Task Replacing_the_model_disposes_the_old_one_and_uses_the_new()
    {
        var swap = new SwappableSpeechToText();
        var first = new Echo { Reply = "one" };
        var second = new Echo { Reply = "two" };
        swap.Set(Task.FromResult<ISpeechToText>(first));
        Assert.Equal("one", await swap.TranscribeAsync(new float[10], CancellationToken.None));
        swap.Set(Task.FromResult<ISpeechToText>(second));
        Assert.True(first.Disposed);
        Assert.Equal("two", await swap.TranscribeAsync(new float[10], CancellationToken.None));
    }

    sealed class TwoSpeed : ISpeechToText
    {
        public Task<string> TranscribeAsync(float[] a, CancellationToken ct) => Task.FromResult("full");
        public Task<string> PreviewAsync(float[] a, CancellationToken ct) => Task.FromResult("quick");
    }

    [Fact]
    public async Task Live_word_previews_reach_the_models_quick_pass()
    {
        var swap = new SwappableSpeechToText();
        swap.Set(Task.FromResult<ISpeechToText>(new TwoSpeed()));
        Assert.Equal("quick", await swap.PreviewAsync(new float[10], CancellationToken.None));
        Assert.Equal("full", await swap.TranscribeAsync(new float[10], CancellationToken.None));

        swap.Set(Task.FromResult<ISpeechToText>(new Echo { Reply = "plain" }));   // an engine without one previews normally
        Assert.Equal("plain", await swap.PreviewAsync(new float[10], CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_model_load_gives_no_preview_and_no_error()
    {
        var swap = new SwappableSpeechToText();
        swap.Set(Task.FromException<ISpeechToText>(new IOException("no internet")));
        Assert.Equal("", await swap.PreviewAsync(new float[10], CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_the_caller_still_cancels()
    {
        var swap = new SwappableSpeechToText();
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => swap.TranscribeAsync(new float[10], cts.Token));
    }

    [Fact]
    public void Model_files_are_named_per_accuracy()
    {
        Assert.EndsWith("ggml-tiny.en.bin", SpeechModel.PathFor(SpeechAccuracy.Fast));
        Assert.EndsWith("ggml-base.en.bin", SpeechModel.PathFor(SpeechAccuracy.Balanced));
        Assert.EndsWith("ggml-small.en.bin", SpeechModel.PathFor(SpeechAccuracy.Accurate));
    }
}
