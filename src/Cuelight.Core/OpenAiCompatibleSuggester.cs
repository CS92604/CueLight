using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Cuelight.Core;

/// <summary>A provider's "no" (or "can't reach it"), already in words the user can act on.</summary>
public sealed class ProviderApiException : Exception
{
    public ProviderApiException(string message, int? status = null, Exception? inner = null) : base(message, inner) =>
        Status = status;

    public int? Status { get; }
}

/// <summary>The words shown for a failure of any kind of provider.</summary>
public static class SuggesterErrors
{
    public static string Describe(Exception ex) => ex is ProviderApiException p ? p.Message : ClaudeSuggester.Describe(ex);
}

/// <summary>
/// Talks to any service that offers the OpenAI chat API: OpenAI itself (ChatGPT's models), Google's Gemini through its
/// OpenAI-compatible address, xAI's Grok, and others (OpenRouter, Groq, a local Ollama...). The message is laid out as
/// the Claude one is, the unchanging front of the conversation first and what changes last, because these services
/// also remember a repeated front (and charge less for it).
///
/// Providers differ in small ways about which settings they accept. Sent first: a low reasoning effort (for speed), a
/// request for token counts, and the output limit under the name the provider is known to use. If a provider answers
/// 400 naming one of them, the request is sent again without that setting or with the other name, and the app
/// remembers that for the model.
/// </summary>
public sealed class OpenAiCompatibleSuggester : ISuggester
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private const int MaxAttempts = 4;
    private const string UserAgent = "Cuelight/1.0";   // some gateways turn away a request that doesn't say who sent it

    private readonly Func<Provider, string?> _apiKey;
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly int _maxTokens;
    private readonly ConcurrentDictionary<string, Quirks> _quirks = new();

    /// <param name="apiKey">The saved key for a provider.</param>
    /// <param name="handler">Lets tests stand in for the network.</param>
    /// <param name="baseUrl">Replaces the provider's address (tests point it at a local server).</param>
    /// <param name="maxTokens">The longest reply allowed, thinking included.</param>
    public OpenAiCompatibleSuggester(Func<Provider, string?> apiKey, HttpMessageHandler? handler = null, string? baseUrl = null, int maxTokens = 4096)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _maxTokens = maxTokens;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = RequestTimeout;   // until the answer starts; a stream that has begun is not cut off by it
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>What a provider turned out not to accept. Remembered per provider and model.</summary>
    private sealed class Quirks
    {
        public volatile bool UseMaxTokens, NoReasoningEffort, NoStreamOptions;
        private bool _renamedTokens;

        public static Quirks For(Provider provider) => new()
        {
            // OpenAI's newer models want max_completion_tokens; Gemini's address and most other services know max_tokens.
            UseMaxTokens = provider is Provider.Gemini or Provider.Other,
            // Other services mostly have no reasoning setting, and some refuse an unknown one: don't offer it there.
            NoReasoningEffort = provider == Provider.Other,
        };

        /// <summary>Change whatever the error names. False if there is nothing left to change.</summary>
        public bool Adapt(string errorBody)
        {
            var b = errorBody.ToLowerInvariant();
            if (!NoReasoningEffort && b.Contains("reasoning")) { NoReasoningEffort = true; return true; }
            if (!NoStreamOptions && (b.Contains("stream_options") || b.Contains("include_usage"))) { NoStreamOptions = true; return true; }
            if (!_renamedTokens && (b.Contains("max_tokens") || b.Contains("max_completion_tokens")))
            {
                _renamedTokens = true;
                UseMaxTokens = !UseMaxTokens;
                return true;
            }
            return false;
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = request.Settings;
        var info = Providers.Get(settings.Provider);
        var root = ResolveRoot(info, settings);
        var model = settings.Model.Trim();
        if (model.Length == 0) throw new ProviderApiException("Enter a model name in Settings.");
        var key = _apiKey(settings.Provider);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"No {info.Name} API key is set. Add one in Settings.");

        var quirks = _quirks.GetOrAdd($"{settings.Provider}|{model}", _ => Quirks.For(settings.Provider));
        string front = string.Concat(Prompting.FrontBlocks(request).Select(b => b.Text));
        string tail = Prompting.TailText(request);

        HttpResponseMessage? response = null;
        for (int attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, root + "/chat/completions")
            {
                Content = new StringContent(Body(model, front, tail, request.RegionPng, settings.ThinkFirst, quirks), Encoding.UTF8, "application/json"),
            };
            if (key != ProviderKeys.NoKey) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            try { response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw Network(info, root, ex);
            }
            if (response.IsSuccessStatusCode) break;

            var body = await ReadBodyAsync(response, ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            response = null;
            if (status == 400 && attempt < MaxAttempts && quirks.Adapt(body))
            {
                AppLog.Warn($"{info.Name} refused the request ({Short(body)}); sending it again without that setting.");
                continue;
            }
            throw Failure(info, model, status, body, request.RegionPng is not null);
        }

        long input = 0, cached = 0, output = 0, chars = 0;
        bool sawUsage = false, started = false;
        string? finish = null;
        using var owned = response!;
        try
        {
            await using var stream = await owned.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await ReadLineAsync(reader, info, ct)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // blank lines, comments, event names
                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root0 = doc.RootElement;
                    if (root0.ValueKind != JsonValueKind.Object) continue;
                    started = true;
                    if (root0.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                        throw new ProviderApiException($"{info.Name} stopped the reply: {Text(err, "message") ?? "unknown error"}");

                    if (root0.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                            && Text(delta, "content") is { Length: > 0 } text)
                        {
                            chars += text.Length;
                            yield return text;
                        }
                        finish = Text(choice, "finish_reason") ?? finish;
                    }
                    if (root0.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        sawUsage = true;
                        long prompt = Number(usage, "prompt_tokens");
                        cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
                            ? Number(details, "cached_tokens") : 0;
                        input = Math.Max(0, prompt - cached);
                        output = Number(usage, "completion_tokens");
                    }
                }
            }
        }
        finally
        {
            // Also runs when the request is cut short (a newer one replaced it): what the provider had already read and
            // written is still billed. When no counts came back, an estimate (about four characters to a token) stands in.
            if (started && request.OnUsage is { } report)
            {
                if (!sawUsage)
                {
                    input = (Prompting.SystemPrompt.Length + front.Length + tail.Length) / 4;
                    output = chars / 4;
                }
                report(new TokenUsage(model, input, 0, cached, output));
            }
        }

        if (finish == "content_filter") yield return "\n(The AI declined to suggest a reply for this.)";
        else if (finish == "length") yield return chars == 0 ? "(The model used up its room while thinking. Try again, or pick a faster model in Settings.)" : " …";
    }

    // -- the request ----------------------------------------------------------------------------

    private string Body(string model, string front, string tail, byte[]? png, bool thinkFirst, Quirks quirks)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", true);
            if (!quirks.NoStreamOptions)
            {
                w.WriteStartObject("stream_options");
                w.WriteBoolean("include_usage", true);
                w.WriteEndObject();
            }
            w.WriteNumber(quirks.UseMaxTokens ? "max_tokens" : "max_completion_tokens", _maxTokens);
            if (!quirks.NoReasoningEffort) w.WriteString("reasoning_effort", thinkFirst ? "medium" : "low");

            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", Prompting.SystemPrompt);
            w.WriteEndObject();

            // Same order as for Claude: the part that repeats, then the picture, then what changes every time.
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            Part(w, front);
            if (png is not null)
            {
                w.WriteStartObject();
                w.WriteString("type", "image_url");
                w.WriteStartObject("image_url");
                w.WriteString("url", "data:image/png;base64," + Convert.ToBase64String(png));
                w.WriteEndObject();
                w.WriteEndObject();
            }
            Part(w, tail);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());

        static void Part(Utf8JsonWriter w, string text)
        {
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
        }
    }

    private string ResolveRoot(ProviderInfo info, Settings settings)
    {
        var address = (_baseUrl ?? (info.IsCustom ? settings.BaseUrl : info.BaseUrl) ?? "").Trim().TrimEnd('/');
        if (address.Length == 0)
            throw new ProviderApiException("Enter the service's address in Settings, for example https://openrouter.ai/api/v1");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ProviderApiException("That address doesn't look right. It should start with https:// (or http:// for a service on this PC).");
        return address;
    }

    // -- reading the reply ----------------------------------------------------------------------

    private static async Task<string?> ReadLineAsync(StreamReader reader, ProviderInfo info, CancellationToken ct)
    {
        try { return await reader.ReadLineAsync(ct); }
        catch (IOException ex) { throw new ProviderApiException($"The connection to {Where(info)} dropped while it was answering. Try again.", inner: ex); }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > 4000 ? text[..4000] : text;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { return ""; }
    }

    // -- failures, in plain words ---------------------------------------------------------------

    private static string Where(ProviderInfo info) => info.IsCustom ? "the service" : info.Name;

    /// <summary>The provider's own explanation, from the error JSON it sent (or the start of whatever it sent).</summary>
    internal static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement;
            if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0) el = el[0];
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.Object && Text(e, "message") is { Length: > 0 } m) return m;
                    if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                }
                if (Text(el, "message") is { Length: > 0 } top) return top;
            }
        }
        catch (JsonException) { }
        return Short(body);
    }

    private static string Short(string body)
    {
        body = body.Trim();
        return body.Length > 240 ? body[..240] + "…" : body;
    }

    internal static ProviderApiException Failure(ProviderInfo info, string model, int status, string body, bool hadImage)
    {
        var who = Where(info);
        var said = ExtractMessage(body);
        var lower = said.ToLowerInvariant();
        string text = status switch
        {
            401 => $"{who} didn't accept that key. Check it and try again.",
            402 => $"{who} says the account needs credit before it can answer.",
            403 => $"That key doesn't have permission to use {who}'s API. {Short(said)}".Trim(),
            404 => info.IsCustom
                ? $"The service couldn't find the model “{model}”, or the address is wrong. Check both in Settings."
                : $"{who} couldn't find the model “{model}” for this key. Pick another model in Settings.",
            429 when lower.Contains("quota") || lower.Contains("credit") || lower.Contains("billing") =>
                $"{who} says this account is out of quota or credit. Add credit with them, or try another key.",
            429 => $"{who} is rate-limiting this key right now. Try again in a moment.",
            >= 500 => $"{who}'s service had a problem. Try again in a moment.",
            400 when hadImage && (lower.Contains("image") || lower.Contains("vision") || lower.Contains("multimodal")) =>
                $"“{model}” can't read pictures, so Type can't use it. Pick another model in Settings, or turn Type off.",
            _ => $"{who} error ({status}): {said}",
        };
        return new ProviderApiException(text, status);
    }

    private static ProviderApiException Network(ProviderInfo info, string root, Exception ex)
    {
        var who = Where(info);
        if (ex is TaskCanceledException or TimeoutException)
            return new ProviderApiException($"{who} took too long to answer. Check your connection and try again.", inner: ex);
        if (ContainsInChain<System.Security.Authentication.AuthenticationException>(ex))
            return new ProviderApiException($"Couldn't make a secure connection to {who}. A proxy, antivirus program or a wrong date and time on this PC can cause that.", inner: ex);
        var host = Uri.TryCreate(root, UriKind.Absolute, out var uri) ? uri.Host : root;
        return new ProviderApiException(info.IsCustom
            ? $"Couldn't reach the service at {host}. Check the address, your internet connection, and any proxy or firewall."
            : $"Couldn't reach {info.Name}. Check your internet connection, and any proxy or firewall.", inner: ex);
    }

    private static bool ContainsInChain<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is T) return true;
        return false;
    }

    // -- checking a key -------------------------------------------------------------------------

    /// <summary>Cheap check that a key is accepted: asks the service for its model list, which costs nothing. A service
    /// with no such list can't be checked, and is let through.</summary>
    public static async Task<(bool Ok, string Message)> ValidateKeyAsync(
        Provider provider, string apiKey, string? address, HttpMessageHandler? handler = null, string? baseUrl = null, CancellationToken ct = default)
    {
        var info = Providers.Get(provider);
        var root = (baseUrl ?? (info.IsCustom ? address : info.BaseUrl) ?? "").Trim().TrimEnd('/');
        if (root.Length == 0) return (false, "Enter the service's address first, for example https://openrouter.ai/api/v1");
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (false, "That address doesn't look right. It should start with https:// (or http:// for a service on this PC).");
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = CheckTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(CheckTimeout + TimeSpan.FromSeconds(5));
            using var message = new HttpRequestMessage(HttpMethod.Get, root + "/models");
            if (apiKey.Trim() is { Length: > 0 } k && k != ProviderKeys.NoKey) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
            using var response = await http.SendAsync(message, limit.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || status is 404 or 405 or 501) return (true, "");   // 404/405: no model list to ask
            var body = await ReadBodyAsync(response, limit.Token);
            return (false, Failure(info, "", status, body, false).Message);
        }
        catch (Exception ex)
        {
            return (false, SuggesterErrors.Describe(ex is ProviderApiException ? ex : Network(info, root, ex)));
        }
    }
}

/// <summary>Checks a key with whichever provider it belongs to.</summary>
public static class KeyValidator
{
    public static Task<(bool Ok, string Message)> ValidateAsync(Provider provider, string apiKey, string? address, CancellationToken ct = default) =>
        provider == Provider.Claude
            ? ClaudeSuggester.ValidateKeyAsync(apiKey, ct: ct)
            : OpenAiCompatibleSuggester.ValidateKeyAsync(provider, apiKey, address, ct: ct);
}

/// <summary>Sends each request to the provider the settings name.</summary>
public sealed class RoutingSuggester : ISuggester
{
    private readonly ISuggester _claude, _compatible;

    public RoutingSuggester(ISuggester claude, ISuggester compatible)
    {
        _claude = claude;
        _compatible = compatible;
    }

    public IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, CancellationToken ct) =>
        request.Settings.Provider == Provider.Claude ? _claude.StreamAsync(request, ct) : _compatible.StreamAsync(request, ct);
}
