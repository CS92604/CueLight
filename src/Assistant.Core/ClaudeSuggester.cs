using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Assistant.Core;

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
        var content = new List<BetaContentBlockParam>();
        if (request.RegionPng is { } png)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
            });
        }
        content.Add(new BetaTextBlockParam { Text = Prompting.BuildUserText(request) });

        var p = new MessageCreateParams
        {
            Model = model,
            MaxTokens = _maxTokens,
            System = Prompting.SystemPrompt,
            Messages = [new BetaMessageParam { Role = Role.User, Content = content }],
        };
        // `effort` is not accepted by Haiku 4.5.
        if (!model.StartsWith("claude-haiku", StringComparison.Ordinal))
            p = p with { OutputConfig = new BetaOutputConfig { Effort = Effort.Low } };
        if (FallbackModels.Contains(model))
            // If the safety classifiers decline, the API re-runs the request on a fallback model.
            p = p with { Betas = [FallbackBeta], Fallbacks = new BetaFallbacksParam(new Default()) };

        string? stopReason = null;
        await foreach (var ev in Client().Beta.Messages.CreateStreaming(p, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var md))
                stopReason = md.Delta.StopReason?.Raw() ?? stopReason;
        }

        if (stopReason == "refusal") yield return "\n(Claude declined to suggest a reply for this.)";
        else if (stopReason == "max_tokens") yield return " …";
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
