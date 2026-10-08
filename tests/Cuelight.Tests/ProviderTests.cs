using System.Net;
using System.Text;
using System.Text.Json;
using Cuelight.Core;

namespace Cuelight.Tests;

/// <summary>A local stand-in for an OpenAI-style chat service (OpenAI, Gemini's compatible address, xAI, Ollama...).</summary>
public sealed class FakeChatServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }
    public readonly List<(string Path, JsonElement Body, Dictionary<string, string> Headers)> Requests = new();

    public int Status = 200;
    public string ErrorBody = "{\"error\":{\"message\":\"nope\"}}";
    public string[] Deltas = { "SAY\n• Sure, ", "sounds good." };
    public string? FinishReason = "stop";
    public (int Prompt, int Cached, int Completion)? Usage = (1000, 800, 50);
    public string? RejectParameter;        // answer 400 naming this parameter whenever the request carries it
    public string? InStreamError;          // send an error object in the middle of the stream
    public int ModelsStatus = 200;

    public FakeChatServer()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        Url = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public List<JsonElement> Bodies() { lock (Requests) return Requests.Where(r => r.Path.EndsWith("/chat/completions")).Select(r => r.Body).ToList(); }

    private static async Task Reply(HttpListenerContext ctx, int status, string body, string type = "application/json")
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        ctx.Response.Close();
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
            var body = raw.Length > 0 ? JsonDocument.Parse(raw).RootElement.Clone() : default;
            lock (Requests) Requests.Add((ctx.Request.Url!.AbsolutePath, body, headers));

            if (ctx.Request.Url!.AbsolutePath.EndsWith("/models"))
            {
                await Reply(ctx, ModelsStatus, ModelsStatus == 200 ? "{\"data\":[]}" : ErrorBody);
                continue;
            }
            if (RejectParameter is { } p && body.ValueKind == JsonValueKind.Object && body.TryGetProperty(p, out _))
            {
                await Reply(ctx, 400, $"{{\"error\":{{\"message\":\"Unsupported parameter: '{p}' is not supported with this model.\"}}}}");
                continue;
            }
            if (Status != 200) { await Reply(ctx, Status, ErrorBody); continue; }

            var sb = new StringBuilder();
            void Data(object o) => sb.Append("data: ").Append(JsonSerializer.Serialize(o)).Append("\n\n");
            sb.Append(": keep-alive\n\n");
            Data(new { choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null } } });
            foreach (var d in Deltas) Data(new { choices = new[] { new { index = 0, delta = new { content = d }, finish_reason = (string?)null } } });
            if (InStreamError is { } e) Data(new { error = new { message = e } });
            Data(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = FinishReason } } });
            if (Usage is { } u)
                Data(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = u.Prompt, completion_tokens = u.Completion, prompt_tokens_details = new { cached_tokens = u.Cached } } });
            sb.Append("data: [DONE]\n\n");
            await Reply(ctx, 200, sb.ToString(), "text/event-stream");
        }
    }

    public void Dispose() => _listener.Close();
}

public class OpenAiCompatibleSuggesterTests
{
    private static async Task<string> Collect(ISuggester s, SuggestionRequest r)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in s.StreamAsync(r, CancellationToken.None)) sb.Append(chunk);
        return sb.ToString();
    }

    private static SuggestionRequest Req(Provider p = Provider.OpenAi, string? model = null, byte[]? png = null,
        bool think = false, Action<TokenUsage>? onUsage = null, string address = "")
    {
        var settings = new Settings { Provider = p, Model = model ?? Providers.Get(p).DefaultModel, ThinkFirst = think, BaseUrl = address };
        return new SuggestionRequest("Them: Can you start Monday?", settings, Trigger.Speech, null, png) { OnUsage = onUsage };
    }

    private static OpenAiCompatibleSuggester Suggester(FakeChatServer server, string? key = "sk-test-key") =>
        new(_ => key, baseUrl: server.Url);

    [Fact]
    public async Task Streams_text_and_sends_the_expected_request()
    {
        using var server = new FakeChatServer();
        var text = await Collect(Suggester(server), Req(png: new byte[] { 1, 2, 3 }));
        Assert.Equal("SAY\n• Sure, sounds good.", text);

        var (path, body, headers) = server.Requests.Single();
        Assert.Equal("/v1/chat/completions", path);
        Assert.Equal("Bearer sk-test-key", headers["authorization"]);
        Assert.Equal("gpt-6.1-sol", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(4096, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _));
        Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());

        var messages = body.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(Prompting.SystemPrompt, messages[0].GetProperty("content").GetString());
        var content = messages[1].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());                           // the repeated front, the picture, what changes
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains("<conversation_so_far>", content[0].GetProperty("text").GetString());
        Assert.Contains("Them: Can you start Monday?", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Contains("<changed>spoken</changed>", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Without_a_picture_the_message_has_only_the_two_text_parts_and_thinking_first_raises_the_effort()
    {
        using var server = new FakeChatServer();
        await Collect(Suggester(server), Req(think: true));
        var body = server.Requests.Single().Body;
        Assert.Equal(2, body.GetProperty("messages")[1].GetProperty("content").GetArrayLength());
        Assert.Equal("medium", body.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData(Provider.OpenAi, "max_completion_tokens", true)]
    [InlineData(Provider.Grok, "max_completion_tokens", true)]
    [InlineData(Provider.Gemini, "max_tokens", true)]
    [InlineData(Provider.Other, "max_tokens", false)]
    public async Task Each_provider_gets_the_limit_name_and_reasoning_setting_it_is_known_to_accept(Provider p, string limit, bool reasoning)
    {
        using var server = new FakeChatServer();
        await Collect(Suggester(server), Req(p, model: "some-model", address: server.Url));
        var body = server.Requests.Single().Body;
        Assert.True(body.TryGetProperty(limit, out _), $"{p} should send {limit}");
        Assert.Equal(reasoning, body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Another_service_uses_the_address_and_model_the_user_typed()
    {
        using var server = new FakeChatServer();
        var suggester = new OpenAiCompatibleSuggester(_ => ProviderKeys.NoKey);          // no address override: Settings.BaseUrl is used
        var text = await Collect(suggester, Req(Provider.Other, "llama3.2", address: server.Url + "/"));
        Assert.Contains("sounds good", text);
        var (path, body, headers) = server.Requests.Single();
        Assert.Equal("/v1/chat/completions", path);
        Assert.Equal("llama3.2", body.GetProperty("model").GetString());
        Assert.False(headers.ContainsKey("authorization"), "a service that needs no key is not sent one");
    }

    [Fact]
    public async Task Token_counts_are_split_into_new_and_remembered_input()
    {
        using var server = new FakeChatServer { Usage = (1000, 800, 50) };
        TokenUsage? seen = null;
        await Collect(Suggester(server), Req(onUsage: u => seen = u));
        Assert.Equal(new TokenUsage("gpt-6.1-sol", 200, 0, 800, 50), seen);
    }

    [Fact]
    public async Task When_no_counts_come_back_the_use_is_estimated_rather_than_lost()
    {
        using var server = new FakeChatServer { Usage = null, Deltas = new[] { new string('x', 400) } };
        TokenUsage? seen = null;
        await Collect(Suggester(server), Req(onUsage: u => seen = u));
        Assert.NotNull(seen);
        Assert.InRange(seen!.Output, 90, 110);
        Assert.True(seen.Input > 100);
        Assert.Equal(0, seen.CacheRead);
    }

    [Fact]
    public async Task A_provider_that_refuses_the_reasoning_setting_is_asked_again_without_it_and_remembered()
    {
        using var server = new FakeChatServer { RejectParameter = "reasoning_effort" };
        var suggester = Suggester(server);
        Assert.Contains("sounds good", await Collect(suggester, Req()));
        Assert.Equal(2, server.Bodies().Count);
        Assert.True(server.Bodies()[0].TryGetProperty("reasoning_effort", out _));
        Assert.False(server.Bodies()[1].TryGetProperty("reasoning_effort", out _));

        await Collect(suggester, Req());                                  // the next request starts without it
        Assert.Equal(3, server.Bodies().Count);
        Assert.False(server.Bodies()[2].TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task A_provider_that_wants_the_other_limit_name_is_asked_again_with_it()
    {
        using var server = new FakeChatServer { RejectParameter = "max_completion_tokens" };
        Assert.Contains("sounds good", await Collect(Suggester(server), Req()));
        Assert.Equal(2, server.Bodies().Count);
        Assert.True(server.Bodies()[1].TryGetProperty("max_tokens", out _));
        Assert.False(server.Bodies()[1].TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public async Task A_provider_that_refuses_the_token_count_request_is_asked_again_without_it()
    {
        using var server = new FakeChatServer { RejectParameter = "stream_options" };
        Assert.Contains("sounds good", await Collect(Suggester(server), Req()));
        Assert.False(server.Bodies()[1].TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task A_400_that_names_nothing_it_can_change_is_reported()
    {
        using var server = new FakeChatServer { Status = 400, ErrorBody = "{\"error\":{\"message\":\"messages: bad shape\"}}" };
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(Suggester(server), Req()));
        Assert.Contains("messages: bad shape", ex.Message);
        Assert.Single(server.Bodies());                                    // not retried in a loop
    }

    [Theory]
    [InlineData(401, "didn't accept that key")]
    [InlineData(403, "doesn't have permission")]
    [InlineData(404, "couldn't find the model")]
    [InlineData(429, "rate-limiting")]
    [InlineData(500, "had a problem")]
    public async Task Failures_are_explained_in_plain_words(int status, string expected)
    {
        using var server = new FakeChatServer { Status = status };
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(Suggester(server), Req()));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("ChatGPT", ex.Message);
        Assert.Equal(status, ex.Status);
        Assert.Equal(ex.Message, SuggesterErrors.Describe(ex));
    }

    [Fact]
    public async Task Running_out_of_credit_is_called_that()
    {
        using var server = new FakeChatServer { Status = 429, ErrorBody = "{\"error\":{\"message\":\"You exceeded your current quota, please check your plan and billing details.\"}}" };
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(Suggester(server), Req(Provider.Grok)));
        Assert.Contains("out of quota or credit", ex.Message);
        Assert.Contains("Grok", ex.Message);
    }

    [Fact]
    public async Task A_model_that_cannot_see_pictures_says_so()
    {
        using var server = new FakeChatServer { Status = 400, ErrorBody = "{\"error\":{\"message\":\"This model does not support image inputs.\"}}" };
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(Suggester(server), Req(Provider.Other, "text-only", png: new byte[] { 1 }, address: server.Url)));
        Assert.Contains("can't read pictures", ex.Message);
        Assert.Contains("turn Type off", ex.Message);
    }

    [Fact]
    public async Task An_error_in_the_middle_of_a_reply_is_reported()
    {
        using var server = new FakeChatServer { InStreamError = "the model is overloaded" };
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(Suggester(server), Req()));
        Assert.Contains("the model is overloaded", ex.Message);
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_is_explained()
    {
        var suggester = new OpenAiCompatibleSuggester(_ => "k", baseUrl: "http://127.0.0.1:1/v1");
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(suggester, Req()));
        Assert.Contains("Couldn't reach", ex.Message);
    }

    [Fact]
    public async Task A_reply_that_hit_its_limit_or_was_filtered_says_so()
    {
        using var cut = new FakeChatServer { FinishReason = "length" };
        Assert.EndsWith("…", await Collect(Suggester(cut), Req()));

        using var thoughtOnly = new FakeChatServer { FinishReason = "length", Deltas = Array.Empty<string>() };
        Assert.Contains("used up its room", await Collect(Suggester(thoughtOnly), Req()));

        using var filtered = new FakeChatServer { FinishReason = "content_filter" };
        Assert.Contains("declined", await Collect(Suggester(filtered), Req()));
    }

    [Fact]
    public async Task Missing_pieces_are_reported_before_anything_is_sent()
    {
        using var server = new FakeChatServer();
        var noKey = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(Suggester(server, key: null), Req()));
        Assert.Contains("ChatGPT API key", noKey.Message);

        var noModel = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(Suggester(server), Req(Provider.Other, model: "  ", address: server.Url)));
        Assert.Contains("model name", noModel.Message);

        var noAddress = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(new OpenAiCompatibleSuggester(_ => "k"), Req(Provider.Other, "m", address: "")));
        Assert.Contains("address", noAddress.Message);

        var badAddress = await Assert.ThrowsAsync<ProviderApiException>(() => Collect(new OpenAiCompatibleSuggester(_ => "k"), Req(Provider.Other, "m", address: "ftp://nope")));
        Assert.Contains("doesn't look right", badAddress.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Requests_go_to_the_provider_the_settings_name()
    {
        using var other = new FakeChatServer();
        var router = new RoutingSuggester(new Tagged("claude"), Suggester(other));
        Assert.Equal("claude", await Collect(router, Req(Provider.Claude, "claude-sonnet-5-5")));
        Assert.Empty(other.Requests);
        Assert.Contains("sounds good", await Collect(router, Req(Provider.Gemini)));
        Assert.Single(other.Requests);
    }

    private sealed class Tagged : ISuggester
    {
        private readonly string _text;
        public Tagged(string text) => _text = text;
        public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return _text;
        }
    }

    [Fact]
    public async Task A_key_is_checked_by_asking_for_the_model_list()
    {
        using var ok = new FakeChatServer();
        Assert.True((await OpenAiCompatibleSuggester.ValidateKeyAsync(Provider.OpenAi, "sk-good", null, baseUrl: ok.Url)).Ok);
        var (path, _, headers) = ok.Requests.Single();
        Assert.Equal("/v1/models", path);
        Assert.Equal("Bearer sk-good", headers["authorization"]);

        using var bad = new FakeChatServer { ModelsStatus = 401 };
        var (isOk, message) = await OpenAiCompatibleSuggester.ValidateKeyAsync(Provider.Grok, "xai-bad", null, baseUrl: bad.Url);
        Assert.False(isOk);
        Assert.Contains("Grok didn't accept that key", message);

        using var noList = new FakeChatServer { ModelsStatus = 404 };
        Assert.True((await OpenAiCompatibleSuggester.ValidateKeyAsync(Provider.Other, "x", noList.Url)).Ok);   // can't be checked: let it through

        Assert.False((await OpenAiCompatibleSuggester.ValidateKeyAsync(Provider.Other, "x", "")).Ok);
        var (_, unreachable) = await OpenAiCompatibleSuggester.ValidateKeyAsync(Provider.OpenAi, "sk-x", null, baseUrl: "http://127.0.0.1:1/v1");
        Assert.Contains("Couldn't reach", unreachable);
    }
}

public class ProviderSettingsTests
{
    [Fact]
    public void Every_provider_but_Other_offers_models_and_a_default_that_is_among_them()
    {
        foreach (var info in Providers.All.Where(p => !p.IsCustom))
        {
            Assert.NotEmpty(info.Models);
            Assert.Contains(info.Models, m => m.Id == info.DefaultModel);
            Assert.True(info.IsClaude || info.BaseUrl!.StartsWith("https://"), $"{info.Name} needs an address");
            Assert.True(info.HasKeyPage);
        }
        var other = Providers.Get(Provider.Other);
        Assert.True(other.IsCustom);
        Assert.Empty(other.Models);
        Assert.False(other.HasKeyPage);
        Assert.Equal(5, Providers.All.Count);
    }

    [Theory]
    [InlineData("sk-ant-api03-abc", Provider.Claude)]
    [InlineData("sk-proj-abc123", Provider.OpenAi)]
    [InlineData("xai-abc123", Provider.Grok)]
    [InlineData("AIzaSyAbc123", Provider.Gemini)]
    public void A_key_is_recognised_by_how_it_starts(string key, Provider expected) =>
        Assert.Equal(expected, Providers.Detect("  " + key + " "));

    [Fact]
    public void An_unfamiliar_key_is_not_guessed_at()
    {
        Assert.Null(Providers.Detect("abc123"));
        Assert.Null(Providers.Detect(""));
        Assert.Null(Providers.Detect(null));
    }

    [Fact]
    public void Claude_models_are_resolved_as_before_and_other_providers_keep_whatever_was_typed()
    {
        Assert.Equal("claude-haiku-5-5", Providers.ResolveModel(Provider.Claude, "claude-haiku-4-5"));
        Assert.Equal(Models.Default, Providers.ResolveModel(Provider.Claude, "gpt-6-luna"));
        Assert.Equal("gpt-6-luna", Providers.ResolveModel(Provider.OpenAi, " gpt-6-luna "));
        Assert.Equal("my-fine-tune", Providers.ResolveModel(Provider.OpenAi, "my-fine-tune"));
        Assert.Equal("gpt-6.1-sol", Providers.ResolveModel(Provider.OpenAi, ""));
        Assert.Equal("", Providers.ResolveModel(Provider.Other, null));
    }

    [Fact]
    public void Switching_provider_remembers_each_ones_model()
    {
        var s = new Settings();                                   // Claude, Sonnet
        s.Model = "claude-opus-5-5";
        s.UseProvider(Provider.OpenAi);
        Assert.Equal(Provider.OpenAi, s.Provider);
        Assert.Equal("gpt-6.1-sol", s.Model);                     // that provider's default the first time
        s.Model = "gpt-6-luna";
        s.UseProvider(Provider.Claude);
        Assert.Equal("claude-opus-5-5", s.Model);                 // and the old choice comes back
        s.UseProvider(Provider.OpenAi);
        Assert.Equal("gpt-6-luna", s.Model);
        s.UseProvider(Provider.OpenAi);                           // no change
        Assert.Equal("gpt-6-luna", s.Model);
    }

    [Fact]
    public void A_settings_file_from_before_providers_existed_still_means_Claude()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuelight-old-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"Model\": \"claude-haiku-4-5\", \"Tone\": \"Direct\" }");
            var loaded = new SettingsStore(dir).Load();
            Assert.Equal(Provider.Claude, loaded.Provider);
            Assert.Equal("claude-haiku-5-5", loaded.Model);
            Assert.Equal(Tone.Direct, loaded.Tone);
            Assert.Equal("", loaded.BaseUrl);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_provider_and_its_models_survive_saving_and_loading()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuelight-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var s = new Settings();
            s.UseProvider(Provider.Other);
            s.BaseUrl = " http://localhost:11434/v1 ";
            s.Model = "llama3.2";
            s.UseProvider(Provider.Grok);
            store.Save(s);

            var back = store.Load();
            Assert.Equal(Provider.Grok, back.Provider);
            Assert.Equal("grok-4.7", back.Model);
            Assert.Equal("http://localhost:11434/v1", back.BaseUrl);
            back.UseProvider(Provider.Other);
            Assert.Equal("llama3.2", back.Model);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Normalizing_keeps_a_typed_model_for_other_providers_and_repairs_a_bad_provider()
    {
        var s = new Settings { Provider = Provider.Gemini, Model = "gemini-9-ultra" }.Normalized();
        Assert.Equal("gemini-9-ultra", s.Model);
        var bad = new Settings { Provider = (Provider)99, Model = "x" }.Normalized();
        Assert.Equal(Provider.Claude, bad.Provider);
        Assert.Equal(Models.Default, bad.Model);
    }
}

public sealed class ProviderKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuelight-keys-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Each_provider_has_its_own_key_and_the_Claude_key_keeps_its_original_file()
    {
        var protector = new UserOnlyFileProtector();
        var keys = new ProviderKeys(_dir, protector);
        Assert.False(keys.Has(Provider.Claude));
        keys.Set(Provider.Claude, " sk-ant-one ");
        keys.Set(Provider.OpenAi, "sk-two");
        Assert.Equal("sk-ant-one", keys.Get(Provider.Claude));
        Assert.Equal("sk-two", keys.Get(Provider.OpenAi));
        Assert.False(keys.Has(Provider.Gemini));

        Assert.True(File.Exists(Path.Combine(_dir, "api-key.bin")));
        Assert.True(File.Exists(Path.Combine(_dir, "api-key-openai.bin")));

        var again = new ProviderKeys(_dir, protector);                         // a later start finds them
        Assert.Equal("sk-ant-one", again.Get(Provider.Claude));
        Assert.Equal("sk-two", again.Get(Provider.OpenAi));

        again.Remove(Provider.OpenAi);
        Assert.False(again.Has(Provider.OpenAi));
        Assert.True(again.Has(Provider.Claude));
        Assert.False(new ProviderKeys(_dir, protector).Has(Provider.OpenAi));
    }

    [Fact]
    public void A_key_saved_by_an_earlier_version_is_the_Claude_key()
    {
        var protector = new UserOnlyFileProtector();
        new ApiKeyStore(_dir, protector).Save("sk-ant-from-before");        // the old single-key store
        Assert.Equal("sk-ant-from-before", new ProviderKeys(_dir, protector).Get(Provider.Claude));
    }
}

public class ProviderPricingTests
{
    [Theory]
    [InlineData("gpt-6.1-sol", 2.0)]
    [InlineData("gpt-6-luna", 0.10)]
    [InlineData("gpt-6-astra", 10.0)]
    [InlineData("grok-4.7", 2.0)]
    [InlineData("grok-4.3", 1.25)]
    [InlineData("gemini-3.1-pro-preview", 2.0)]
    public void A_million_input_tokens_cost_the_listed_price(string model, double perMillion)
    {
        Assert.True(Pricing.TryGet(model, out var price));
        Assert.Equal((decimal)perMillion, price.Cost(new TokenUsage(model, 1_000_000, 0, 0, 0)));
    }

    [Fact]
    public void Remembered_input_costs_the_cached_rate_and_unknown_models_are_counted_but_not_priced()
    {
        Assert.True(Pricing.TryGet("gpt-6.1-sol", out var sol));
        Assert.Equal(0.10m, sol.Cost(new TokenUsage("gpt-6.1-sol", 0, 0, 1_000_000, 0)));

        var meter = new UsageMeter();
        meter.Add(new TokenUsage("llama3.2", 1000, 0, 0, 100));
        var u = meter.Snapshot();
        Assert.True(u.Unpriced);
        Assert.Equal(0m, u.Cost);
        Assert.Equal(1000, u.Input);
        Assert.Equal(100, u.Output);
    }
}
