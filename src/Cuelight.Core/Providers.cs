namespace Cuelight.Core;

/// <summary>
/// Whose AI answers. Claude is called through Anthropic's own SDK (which lets the app use Claude's prompt
/// memory); the others are called through the OpenAI-style chat API that they, and many other services, offer.
/// </summary>
public enum Provider { Claude, OpenAi, Gemini, Grok, Other }

/// <summary>What the app knows about one provider: where to get a key, where to send requests and which models to offer.</summary>
/// <param name="Name">What the app calls it ("ChatGPT").</param>
/// <param name="Company">Who runs it ("OpenAI"); keys are only ever sent to them.</param>
/// <param name="KeyUrl">The page where a key is created, or empty when there isn't one.</param>
/// <param name="KeyHost">That page's address as shown to the user.</param>
/// <param name="KeyHint">What the key box shows before anything is typed.</param>
/// <param name="BaseUrl">Where chat requests go (OpenAI-style providers), or null: Claude uses its SDK, and "Other" is typed in.</param>
public sealed record ProviderInfo(
    Provider Id, string Name, string Company, string KeyUrl, string KeyHost, string KeyHint,
    string? BaseUrl, string DefaultModel, IReadOnlyList<ModelChoice> Models, string Blurb)
{
    public bool IsClaude => Id == Provider.Claude;

    /// <summary>"Other" is any OpenAI-style service: its address and model name are typed in.</summary>
    public bool IsCustom => Id == Provider.Other;

    public bool HasKeyPage => KeyUrl.Length > 0;
}

public static class Providers
{
    // Model ids and list prices as published by each provider in October 2026. Models are replaced often, so the app
    // also takes any model id typed in (Settings), and a model it has no price for is still counted, in tokens.
    public static readonly IReadOnlyList<ProviderInfo> All = new[]
    {
        new ProviderInfo(Provider.Claude, "Claude", "Anthropic",
            "https://console.anthropic.com/settings/keys", "console.anthropic.com", "sk-ant-…",
            null, Models.Default, Models.All,
            "Anthropic's Claude. It remembers the earlier part of the conversation between suggestions, which keeps the cost down."),

        new ProviderInfo(Provider.OpenAi, "ChatGPT", "OpenAI",
            "https://platform.openai.com/api-keys", "platform.openai.com", "sk-…",
            "https://api.openai.com/v1", "gpt-6.1-sol",
            new[]
            {
                new ModelChoice("gpt-6-astra", "GPT-6 Astra", "Best replies · 5× the cost",
                    "OpenAI's most capable model: the best replies. Slower, and about five times the price of GPT-6.1 Sol."),
                new ModelChoice("gpt-6.1-sol", "GPT-6.1 Sol", "Recommended",
                    "The recommended balance: very good replies at a moderate price."),
                new ModelChoice("gpt-6-luna", "GPT-6 Luna", "Cheapest · 1/20 the cost",
                    "The fastest and cheapest, about a twentieth of Sol's price. Less nuanced."),
            },
            "OpenAI's GPT models, the ones behind ChatGPT. You need an OpenAI API key; a ChatGPT subscription doesn't include one."),

        new ProviderInfo(Provider.Gemini, "Gemini", "Google",
            "https://aistudio.google.com/apikey", "aistudio.google.com", "AIza…",
            "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash",
            new[]
            {
                new ModelChoice("gemini-3.1-pro-preview", "Gemini 3.1 Pro (preview)", "Best replies",
                    "Google's most capable Gemini: the best replies, slower and pricier than Flash. Still a preview model."),
                new ModelChoice("gemini-3.8-flash", "Gemini 3.8 Flash", "Recommended",
                    "The recommended balance: quick, good replies, low price."),
            },
            "Google's Gemini models, through the Gemini API. A key from Google AI Studio works."),

        new ProviderInfo(Provider.Grok, "Grok", "xAI",
            "https://console.x.ai", "console.x.ai", "xai-…",
            "https://api.x.ai/v1", "grok-4.7",
            new[]
            {
                new ModelChoice("grok-4.7", "Grok 4.7", "Recommended",
                    "xAI's newest Grok: the best replies. It always reasons a little before answering."),
                new ModelChoice("grok-4.3", "Grok 4.3", "Cheaper",
                    "An older Grok at about half the price."),
            },
            "xAI's Grok models. You need an xAI API key."),

        new ProviderInfo(Provider.Other, "Other", "the service you enter",
            "", "", "API key (if it needs one)",
            null, "", Array.Empty<ModelChoice>(),
            "Any service that speaks the OpenAI chat API: OpenRouter, Groq, Together, a model running on this PC with Ollama, and many more. Enter its address and a model name."),
    };

    public static ProviderInfo Get(Provider provider) => All.First(p => p.Id == provider);

    /// <summary>Which provider a key looks like it belongs to, going by how its provider's keys start; null if unsure.</summary>
    public static Provider? Detect(string? key)
    {
        key = (key ?? "").Trim();
        if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return Provider.Claude;
        if (key.StartsWith("xai-", StringComparison.Ordinal)) return Provider.Grok;
        if (key.StartsWith("AIza", StringComparison.Ordinal)) return Provider.Gemini;
        if (key.StartsWith("sk-", StringComparison.Ordinal)) return Provider.OpenAi;
        return null;
    }

    /// <summary>
    /// The model to actually use for a saved id. Claude's list is fixed (retired ids map to their replacement, anything
    /// unknown to the default). For the others any id the user typed is kept, and only an empty one falls back to
    /// the provider's default.
    /// </summary>
    public static string ResolveModel(Provider provider, string? id)
    {
        if (provider == Provider.Claude) return Models.Resolve(id);
        id = (id ?? "").Trim();
        return id.Length > 0 ? id : Get(provider).DefaultModel;
    }
}
