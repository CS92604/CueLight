using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Cuelight.Core;

public interface ISuggester
{
    /// <summary>Stream the text of a suggestion as Claude writes it.</summary>
    IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, CancellationToken ct);
}

/// <summary>Talks to the Claude API with the key the user entered.</summary>
public sealed class ClaudeSuggester : ISuggester
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    // Models for which the server-side `fallbacks: "default"` parameter applies.
    private static readonly HashSet<string> FallbackModels = new()
    {
        "claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5",
    };

    private readonly Func<string?> _apiKey;
    private readonly string? _baseUrl;
    private readonly int _maxTokens;
    private AnthropicClient? _client;
    private string? _clientKey;

    public ClaudeSuggester(Func<string?> apiKey, int maxTokens = 2048, string? baseUrl = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _maxTokens = maxTokens; // thinking tokens count toward this
    }

    private AnthropicClient Client()
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Claude API key is set.");
        if (_client is null || _clientKey != key)
        {
            // A reply is a few hundred tokens; if nothing has come back in two minutes the connection is stuck.
            _client = _baseUrl is null
                ? new AnthropicClient { ApiKey = key, Timeout = RequestTimeout }
                : new AnthropicClient { ApiKey = key, BaseUrl = _baseUrl, Timeout = RequestTimeout };
            _clientKey = key;
        }
        return _client;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = request.Settings.Model;

        // Order matters for cost. Claude remembers a request's front part, and pays only a tenth (a twentieth
        // on some models) of the normal price to read it again, but only if that front is identical next time.
        // So: the instructions, then the conversation (marked as the end of what to remember), and only then the
        // things that differ every time (the screen picture, the direction, the task).
        var content = new List<BetaContentBlockParam>();
        foreach (var block in Prompting.FrontBlocks(request))
        {
            var part = new BetaTextBlockParam { Text = block.Text };
            if (block.CacheAfter) part = part with { CacheControl = new BetaCacheControlEphemeral() };
            content.Add(part);   // the marker is left off (not sent as null) on every block that doesn't carry one
        }
        if (request.RegionPng is { } png)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
            });
        }
        content.Add(new BetaTextBlockParam { Text = Prompting.TailText(request) });

        var system = new List<BetaTextBlockParam>
        {
            new() { Text = Prompting.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
        };
        var thinkFirst = request.Settings.ThinkFirst;

        // Up-front thinking only adds waiting time before a quick conversational reply, so it is turned off where the
        // model allows it. (Opus 5.5 can't have thinking turned off; low effort keeps it short.) If a request with this
        // override is ever refused, it is sent again without it, and the override is dropped for that model.
        string? ThinkingOverride() =>
            thinkFirst || Refused(model) ? null
            : model.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal) ? "between_tools"
            : model.StartsWith("claude-haiku-5", StringComparison.Ordinal) ? "disabled"
            : null;

        MessageCreateParams Build(string? thinking)
        {
            var p = new MessageCreateParams
            {
                Model = model,
                MaxTokens = _maxTokens,
                System = system,
                Messages = [new BetaMessageParam { Role = Role.User, Content = content }],
            };
            // `effort` is not accepted by Haiku 4.5 (Haiku 5.5 and the other models take it).
            if (!model.StartsWith("claude-haiku-4", StringComparison.Ordinal))
                p = p with { OutputConfig = new BetaOutputConfig { Effort = thinkFirst ? Effort.Medium : Effort.Low } };
            if (thinking == "between_tools") p = p with { Thinking = new BetaThinkingConfigBetweenTools() };
            else if (thinking == "disabled") p = p with { Thinking = new BetaThinkingConfigDisabled() };
            if (FallbackModels.Contains(model))
                // If the safety classifiers decline, the API re-runs the request on a fallback model.
                p = p with { Betas = [FallbackBeta], Fallbacks = new BetaFallbacksParam(new Default()) };
            return p;
        }

        string? stopReason = null;
        long input = 0, written = 0, read = 0, output = 0, chars = 0;
        bool started = false;
        try
        {
            await foreach (var ev in Events(Build, ThinkingOverride(), model, ct))
            {
                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    chars += text.Text.Length;
                    yield return text.Text;
                }
                else if (ev.TryPickStart(out var start))
                {
                    var u = start.Message.Usage;
                    input = u.InputTokens;
                    written = u.CacheCreationInputTokens ?? 0;
                    read = u.CacheReadInputTokens ?? 0;
                    output = u.OutputTokens;
                    started = true;
                }
                else if (ev.TryPickDelta(out var md))
                {
                    stopReason = md.Delta.StopReason?.Raw() ?? stopReason;
                    var u = md.Usage;   // running totals for the whole request
                    input = u.InputTokens ?? input;
                    written = u.CacheCreationInputTokens ?? written;
                    read = u.CacheReadInputTokens ?? read;
                    output = u.OutputTokens;
                }
            }
        }
        finally
        {
            // Also runs when the request is cut short (a newer one replaced it): what Claude had already read and
            // written is still billed, so it still counts.
            if (started && request.OnUsage is { } report)
            {
                // A cut-off request never reports its final total; roughly four characters make a token.
                if (stopReason is null) output = Math.Max(output, chars / 4);
                report(new TokenUsage(model, input, written, read, output));
            }
        }

        if (stopReason == "refusal") yield return "\n(Claude declined to suggest a reply for this.)";
        else if (stopReason == "max_tokens") yield return " …";
    }

    // Models for which the "thinking" override was refused by the API; they are sent without it from then on.
    private readonly HashSet<string> _thinkingRefused = new();

    private bool Refused(string model) { lock (_thinkingRefused) return _thinkingRefused.Contains(model); }

    /// <summary>Streams a request. If the API refuses it (a 400) before sending anything while a thinking override
    /// was set, sends it once more without the override.</summary>
    private async IAsyncEnumerable<BetaRawMessageStreamEvent> Events(
        Func<string?, MessageCreateParams> build, string? thinking, string model, [EnumeratorCancellation] CancellationToken ct)
    {
        var it = Client().Beta.Messages.CreateStreaming(build(thinking), ct).GetAsyncEnumerator(ct);
        bool has;
        try { has = await it.MoveNextAsync(); }
        catch (AnthropicBadRequestException ex) when (thinking is not null)
        {
            AppLog.Warn($"Claude refused the request with thinking turned off ({ex.Message}); sending it without that setting from now on.");
            lock (_thinkingRefused) _thinkingRefused.Add(model);
            await it.DisposeAsync();
            it = Client().Beta.Messages.CreateStreaming(build(null), ct).GetAsyncEnumerator(ct);
            has = await it.MoveNextAsync();
        }
        try
        {
            while (has)
            {
                yield return it.Current;
                has = await it.MoveNextAsync();
            }
        }
        finally { await it.DisposeAsync(); }
    }

    /// <summary>Cheap check that a key is accepted (lists models; costs nothing).</summary>
    public static async Task<(bool Ok, string Message)> ValidateKeyAsync(
        string apiKey, string? baseUrl = null, CancellationToken ct = default)
    {
        try
        {
            // A blocked network (firewall, proxy) must not leave "Checking your key…" spinning for minutes.
            var client = baseUrl is null
                ? new AnthropicClient { ApiKey = apiKey.Trim(), Timeout = CheckTimeout, MaxRetries = 1 }
                : new AnthropicClient { ApiKey = apiKey.Trim(), BaseUrl = baseUrl, Timeout = CheckTimeout, MaxRetries = 1 };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(CheckTimeout + TimeSpan.FromSeconds(5));
            await client.Models.List(cancellationToken: limit.Token);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>A short, human message for an API failure.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => "Claude didn't accept that key. Check it and try again.",
        AnthropicForbiddenException => "That key doesn't have permission to use the Claude API.",
        AnthropicRateLimitException => "Claude is rate-limiting this key right now. Try again in a moment.",
        Anthropic5xxException => "Claude's service had a problem. Try again in a moment.",
        AnthropicNotFoundException => "Claude couldn't find that model for this key. Pick another model in Settings.",
        AnthropicApiException a => $"Claude API error: {a.Message}",
        _ when Contains<System.Security.Authentication.AuthenticationException>(ex) =>
            "Couldn't make a secure connection to Claude. A proxy, antivirus program or a wrong date and time on this PC can cause that.",
        AnthropicIOException or HttpRequestException =>
            "Couldn't reach the Claude API. Check your internet connection, and any proxy or firewall.",
        TaskCanceledException or TimeoutException or OperationCanceledException =>
            "Claude took too long to answer. Check your connection and try again.",
        _ => ex.Message,
    };

    private static bool Contains<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is T) return true;
        return false;
    }
}
