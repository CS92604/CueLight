using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cuelight.Core;

public enum Professionalism { VeryCasual, Casual, Professional, Formal }
public enum Proficiency { Simple, Everyday, Fluent, Advanced }
public enum Tone { Warm, Neutral, Direct, Diplomatic, Confident }
public enum ReplyLength { Brief, Short, Detailed }
public enum SpeechAccuracy { Fast, Balanced, Accurate }

/// <summary>How the watched text area reaches the AI. Fast: the words on it are read on this PC and only that text is
/// sent. Detailed: a picture of it is sent, so the AI also sees layout, colours and who wrote what.</summary>
public enum ScreenReading { Fast, Detailed }

/// <summary>User preferences. Saved as JSON; applies to the next suggestion.</summary>
public sealed class Settings
{
    public Professionalism Professionalism { get; set; } = Professionalism.Professional;
    /// <summary>How much jargon to use and how deeply to explain things.</summary>
    public Proficiency Proficiency { get; set; } = Proficiency.Fluent;
    public Tone Tone { get; set; } = Tone.Warm;
    public ReplyLength Length { get; set; } = ReplyLength.Short;
    /// <summary>Suggestions per section (1-3).</summary>
    public int Options { get; set; } = 2;
    /// <summary>Empty = same language as the other person.</summary>
    public string ReplyLanguage { get; set; } = "";
    /// <summary>Free text: who the user is, words to avoid, background for the conversation.</summary>
    public string CustomInstructions { get; set; } = "";

    /// <summary>Whose AI answers. Claude unless the user chose another provider; a settings file from before the choice existed
    /// has no value and so means Claude.</summary>
    public Provider Provider { get; set; } = Provider.Claude;
    /// <summary>The model for the current provider (an id like <c>claude-sonnet-5-5</c> or <c>gpt-6.1-sol</c>).</summary>
    public string Model { get; set; } = Models.Default;
    /// <summary>The address of an OpenAI-style service, used when the provider is <see cref="Provider.Other"/>.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>The model last used with each provider (by provider name), so switching back finds it again.</summary>
    public Dictionary<string, string> ProviderModels { get; set; } = new();
    /// <summary>Let the AI think before it replies: slower to start, better on hard questions. Off by default,
    /// because in a live conversation the first words of a reply matter more.</summary>
    public bool ThinkFirst { get; set; }
    public bool UseMicrophone { get; set; }
    public bool AutoSuggest { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>TYPE side on: a text area is selected and watched, and replies to it are suggested.
    /// Off means the app only listens to the conversation (SAY).</summary>
    public bool TypeEnabled { get; set; }
    /// <summary>Windows only: keep every window of the app out of screen captures and screen shares.</summary>
    public bool HideFromCapture { get; set; }
    public SpeechAccuracy SpeechAccuracy { get; set; } = SpeechAccuracy.Balanced;
    /// <summary>What is sent for the watched text area: its words (read on this PC) or a picture of it. Detailed unless
    /// chosen, which is also what a settings file from before the choice existed means.</summary>
    public ScreenReading ScreenReading { get; set; } = ScreenReading.Detailed;

    /// <summary>
    /// Settings for a first launch. A PC with few processor cores gets the fastest speech model, so
    /// transcription keeps up with the conversation; everything else is the normal default.
    /// </summary>
    public static Settings ForFirstRun(int logicalProcessors) =>
        new() { SpeechAccuracy = logicalProcessors <= 4 ? SpeechAccuracy.Fast : SpeechAccuracy.Balanced };

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>Switch to another provider, remembering the model of the one being left and restoring the one last used
    /// with the new provider (or its default).</summary>
    public void UseProvider(Provider provider)
    {
        if (provider == Provider) return;
        ProviderModels ??= new();
        ProviderModels[Provider.ToString()] = Model;
        Provider = provider;
        Model = ProviderModels.TryGetValue(provider.ToString(), out var remembered) && !string.IsNullOrWhiteSpace(remembered)
            ? remembered
            : Providers.Get(provider).DefaultModel;
    }

    public Settings Normalized()
    {
        var s = Clone();
        s.Options = Math.Clamp(s.Options, 1, 3);
        s.ReplyLanguage = (s.ReplyLanguage ?? "").Trim();
        s.CustomInstructions = (s.CustomInstructions ?? "").Trim();
        if (!Enum.IsDefined(Provider)) s.Provider = Provider.Claude;
        s.Model = Providers.ResolveModel(s.Provider, s.Model);
        s.BaseUrl = (s.BaseUrl ?? "").Trim();
        s.ProviderModels ??= new();
        if (!Enum.IsDefined(Professionalism)) s.Professionalism = Professionalism.Professional;
        if (!Enum.IsDefined(Proficiency)) s.Proficiency = Proficiency.Fluent;
        if (!Enum.IsDefined(Tone)) s.Tone = Tone.Warm;
        if (!Enum.IsDefined(Length)) s.Length = ReplyLength.Short;
        if (!Enum.IsDefined(SpeechAccuracy)) s.SpeechAccuracy = SpeechAccuracy.Balanced;
        if (!Enum.IsDefined(ScreenReading)) s.ScreenReading = ScreenReading.Detailed;
        return s;
    }

    /// <summary>The style block that goes into every request.</summary>
    public string ToPrompt()
    {
        var s = Normalized();
        var lines = new List<string>
        {
            $"- Formality: {Describe(s.Professionalism)}",
            $"- Jargon and depth of explanation: {Describe(s.Proficiency)}",
            $"- Tone: {Describe(s.Tone)}",
            $"- Length: {Describe(s.Length)}",
            $"- Options: up to {s.Options} per section",
            $"- Reply language: {(s.ReplyLanguage.Length > 0 ? s.ReplyLanguage : "the same language the other person is using")}",
        };
        if (s.CustomInstructions.Length > 0) lines.Add($"- About me / extra instructions from me: {s.CustomInstructions}");
        return string.Join("\n", lines);
    }

    public static string Describe(Professionalism v) => v switch
    {
        Professionalism.VeryCasual => "very casual, like texting a friend: contractions, relaxed phrasing, light slang is fine",
        Professionalism.Casual => "casual and friendly, like talking to a colleague you get along with",
        Professionalism.Professional => "professional: polite and clear, no slang, still natural and not stiff",
        _ => "formal: courteous, precise and respectful, as with a senior stakeholder or in a formal setting",
    };

    public static string Describe(Proficiency v) => v switch
    {
        Proficiency.Simple => "plain language with no jargon. Use everyday words, define any unavoidable term in a few words, and explain things simply and from the ground up, as if to a newcomer to the topic",
        Proficiency.Everyday => "mostly plain language with only common, widely known terms. Briefly explain anything technical, with short, practical explanations",
        Proficiency.Fluent => "normal professional vocabulary for the topic. Use standard field terminology without defining it, assume shared background, and explain at a working level of detail",
        _ => "specialist level. Use precise technical terminology and jargon freely, skip the basics, and go into depth where it matters: mechanisms, trade-offs and specifics. (This is about depth and vocabulary, not length)",
    };

    /// <summary>One line shown under the Proficiency control in Settings.</summary>
    public static string Caption(Proficiency v) => v switch
    {
        Proficiency.Simple => "Plain words, no jargon. Explains things from the ground up.",
        Proficiency.Everyday => "Mostly plain words. A few common terms, briefly explained.",
        Proficiency.Fluent => "Normal professional vocabulary. Assumes shared background.",
        _ => "Specialist jargon and depth. Skips the basics.",
    };

    public static string Describe(Tone v) => v switch
    {
        Tone.Warm => "warm and personable",
        Tone.Neutral => "neutral and matter-of-fact",
        Tone.Direct => "direct and to the point, without padding",
        Tone.Diplomatic => "diplomatic and tactful, softening anything that could land badly",
        _ => "confident and assertive, without being aggressive",
    };

    public static string Describe(ReplyLength v) => v switch
    {
        ReplyLength.Brief => "brief: one short sentence per option",
        ReplyLength.Short => "short: one or two sentences per option",
        _ => "detailed: two to four sentences per option, with a reason or example placeholder where it helps",
    };
}

/// <summary>The Claude models the app offers.</summary>
public sealed record ModelChoice(string Id, string Name, string Blurb, string Tip = "", bool SeesPictures = true);

public static class Models
{
    /// <summary>Sonnet: very good replies at half the price of Opus. Cost matters here because the app asks Claude
    /// again after nearly every sentence it hears.</summary>
    public const string Default = "claude-sonnet-5-5";

    public static readonly IReadOnlyList<ModelChoice> All = new[]
    {
        new ModelChoice("claude-opus-5-5", "Claude Opus 5.5", "Best replies · 2× the cost",
            "The most capable model: the best replies. A little slower, and it costs twice as much as Sonnet for each suggestion."),
        new ModelChoice("claude-sonnet-5-5", "Claude Sonnet 5.5", "Recommended",
            "The recommended balance: very good replies, quick, and half the cost of Opus."),
        new ModelChoice("claude-haiku-5-5", "Claude Haiku 5.5", "Cheapest · 1/20 the cost",
            "The fastest and cheapest, about a twentieth of Sonnet's cost. Good for trying the app out or for long calls; less nuanced than the others."),
    };

    // Models the app used to offer, mapped to the one that replaces them (saved settings may still name them).
    private static readonly Dictionary<string, string> Retired = new(StringComparer.Ordinal)
    {
        ["claude-haiku-4-5"] = "claude-haiku-5-5",
    };

    /// <summary>The model to actually use for a saved id: itself if the app offers it, its replacement if it was
    /// retired from the list, otherwise the default.</summary>
    public static string Resolve(string? id)
    {
        if (id is not null && All.Any(m => m.Id == id)) return id;
        if (id is not null && Retired.TryGetValue(id, out var replacement)) return replacement;
        return Default;
    }
}

/// <summary>Reads and writes settings.json; a missing or unreadable file means defaults.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string? directory = null) =>
        _path = Path.Combine(directory ?? AppPaths.ConfigDirectory, "settings.json");

    /// <summary>False on the very first launch (nothing has been saved yet).</summary>
    public bool Exists => File.Exists(_path);

    public Settings Load()
    {
        try
        {
            return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path), Json) ?? new Settings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Settings();
        }
    }

    /// <summary>Writes settings.json (to a temporary file first, so a power cut can't leave half a file).
    /// Returns false, and logs, if the folder can't be written; settings still apply for this session.</summary>
    public bool Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalized(), Json));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Couldn't save settings", ex);
            return false;
        }
    }
}

public static class AppPaths
{
    public const string AppName = "Cuelight";

    /// <summary>What the app was called before it was renamed: its folders were named after that.</summary>
    public const string LegacyAppName = "Claude Live Assistant";

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// Moves the folders of the old name (saved key, settings, speech model, log) to the new one, so an update from the
    /// old name keeps everything. Must run before anything creates the new folders. It does nothing when there is no
    /// old folder or the new one already exists, and a folder it can't move is simply left (the app starts fresh).
    /// </summary>
    public static void MigrateLegacyFolders()
    {
        MigrateLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        MigrateLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    public static void MigrateLegacyFolder(string parent)
    {
        try
        {
            var old = Path.Combine(parent, LegacyAppName);
            var now = Path.Combine(parent, AppName);
            if (Directory.Exists(old) && !Directory.Exists(now)) Directory.Move(old, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
