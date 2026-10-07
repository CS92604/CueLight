using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Core;

namespace Assistant.Tests;

/// <summary>Runs the real Anthropic SDK against a local fake server to check the request we send
/// and that streaming responses are parsed.</summary>
public sealed class FakeClaudeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }
    public readonly List<(string Path, JsonElement Body, Dictionary<string, string> Headers)> Requests = new();
    public string StopReason = "end_turn";
    public int Status = 200;
    public string[] Deltas = { "SAY\n• Sure, ", "sounds good.\nTYPE\n• Works for me!" };
    // What the "API" reports having used: input / cache written / cache read tokens at the start, output tokens at the end.
    public int InputTokens = 10, CacheWritten, CacheRead, OutputTokens = 12;

    public FakeClaudeServer()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            var headers = ctx.Request.Headers.AllKeys.ToDictionary(k => k!.ToLowerInvariant(), k => ctx.Request.Headers[k]!);
            lock (Requests) Requests.Add((ctx.Request.Url!.PathAndQuery, raw.Length > 0 ? JsonDocument.Parse(raw).RootElement.Clone() : default, headers));

            if (Status != 200)
            {
                ctx.Response.StatusCode = Status;
                ctx.Response.ContentType = "application/json";
                var err = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
                await ctx.Response.OutputStream.WriteAsync(err);
                ctx.Response.Close();
                continue;
            }
            if (ctx.Request.Url!.AbsolutePath.EndsWith("/models"))
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[],\"has_more\":false,\"first_id\":null,\"last_id\":null}"));
                ctx.Response.Close();
                continue;
            }

            ctx.Response.ContentType = "text/event-stream";
            var sb = new StringBuilder();
            void Ev(string name, object data) => sb.Append("event: ").Append(name).Append("\ndata: ").Append(JsonSerializer.Serialize(data)).Append("\n\n");
            Ev("message_start", new { type = "message_start", message = new { id = "msg_1", type = "message", role = "assistant", model = "m", content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = InputTokens, cache_creation_input_tokens = CacheWritten, cache_read_input_tokens = CacheRead, output_tokens = 1 } } });
            Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
            foreach (var d in Deltas) Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = d } });
            Ev("content_block_stop", new { type = "content_block_stop", index = 0 });
            Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = StopReason, stop_sequence = (string?)null }, usage = new { output_tokens = OutputTokens } });
            Ev("message_stop", new { type = "message_stop" });
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

public class ClaudeSuggesterTests
{
    static async Task<string> Collect(ClaudeSuggester s, SuggestionRequest r)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in s.StreamAsync(r, CancellationToken.None)) sb.Append(chunk);
        return sb.ToString();
    }

    static SuggestionRequest Req(string model = "claude-opus-5-5", byte[]? png = null, Trigger t = Trigger.Speech)
        => new("Them: Can you start Monday?", new Settings { Model = model }, t, null, png);

    [Fact]
    public async Task Streams_text_and_sends_the_expected_request()
    {
        using var server = new FakeClaudeServer();
        var suggester = new ClaudeSuggester(() => "sk-ant-test-key", baseUrl: server.Url);
        var text = await Collect(suggester, Req(png: new byte[] { 0x89, 0x50, 0x4E, 0x47 }, t: Trigger.Speech | Trigger.Text));

        Assert.Equal("SAY\n• Sure, sounds good.\nTYPE\n• Works for me!", text);
        var (path, body, headers) = Assert.Single(server.Requests);
        Assert.Equal("sk-ant-test-key", headers["x-api-key"]);
        Assert.Contains("server-side-fallback-2026-07-01", headers["anthropic-beta"]);
        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
                Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", body.GetProperty("fallbacks").GetString());

        // The instructions come first and are marked for Claude to remember; the picture and the task come last.
        var system = body.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Contains("SAY", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());

        var content = body.GetProperty("messages")[0].GetProperty("content");
        var last = content.GetArrayLength() - 1;
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains("<style_settings>", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[last - 1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[last - 1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("text", content[last].GetProperty("type").GetString());
        Assert.Contains("<changed>spoken, written</changed>", content[last].GetProperty("text").GetString());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task Haiku_gets_no_effort_and_no_fallbacks()
    {
        using var server = new FakeClaudeServer();
        await Collect(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req("claude-haiku-4-5"));
        var (_, body, headers) = Assert.Single(server.Requests);
        Assert.False(body.TryGetProperty("output_config", out _));
        Assert.False(body.TryGetProperty("fallbacks", out _));
        Assert.False(headers.TryGetValue("anthropic-beta", out var beta) && beta!.Contains("fallback"));
    }

    [Fact]
    public async Task A_refusal_is_reported_to_the_user()
    {
        using var server = new FakeClaudeServer { StopReason = "refusal", Deltas = new[] { "" } };
        var text = await Collect(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req());
        Assert.Contains("declined", text);
    }

    [Fact]
    public async Task Hitting_the_token_limit_is_marked()
    {
        using var server = new FakeClaudeServer { StopReason = "max_tokens" };
        Assert.EndsWith("…", await Collect(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req()));
    }

    [Fact]
    public async Task Missing_key_is_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(new ClaudeSuggester(() => null), Req()));
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task Key_validation_accepts_a_good_key_and_explains_a_bad_one()
    {
        using (var ok = new FakeClaudeServer())
            Assert.True((await ClaudeSuggester.ValidateKeyAsync("sk-ant-good", ok.Url)).Ok);
        using var bad = new FakeClaudeServer { Status = 401 };
        var (isOk, message) = await ClaudeSuggester.ValidateKeyAsync("sk-ant-bad", bad.Url);
        Assert.False(isOk);
        Assert.Contains("didn't accept that key", message);
    }
}
