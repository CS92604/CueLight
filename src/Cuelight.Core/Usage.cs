namespace Cuelight.Core;

/// <summary>What one request used, as the provider's API reported it.</summary>
/// <param name="Input">Input tokens billed at the full price (the part that wasn't cached).</param>
/// <param name="CacheWrite">Input tokens stored in the cache by this request (billed a little above full price).</param>
/// <param name="CacheRead">Input tokens served from the cache (billed at a fraction of full price).</param>
/// <param name="Output">Tokens the AI wrote, including any it thought through first.</param>
public sealed record TokenUsage(string Model, long Input, long CacheWrite, long CacheRead, long Output)
{
    public long TotalInput => Input + CacheWrite + CacheRead;
}

/// <summary>A model's list prices, in US dollars per million tokens.</summary>
public sealed record ModelPrice(decimal Input, decimal CacheWrite, decimal CacheRead, decimal Output)
{
    public decimal Cost(TokenUsage u) =>
        (u.Input * Input + u.CacheWrite * CacheWrite + u.CacheRead * CacheRead + u.Output * Output) / 1_000_000m;
}

/// <summary>
/// The providers' published prices for the models the app offers. The figure the app shows is an estimate from these;
/// the provider's own dashboard has the exact bill. A model that isn't listed (any model id can be typed in) is still
/// counted, in tokens, and the total says it is incomplete.
/// </summary>
public static class Pricing
{
    // Claude: platform.claude.com/docs/en/about-claude/pricing, checked October 2026 (the 5-minute cache, which is the
    // one the app uses). The others were read from each provider's pricing page in October 2026. They remember a repeated
    // front part of a request on their own and bill it at the cached rate; their usage reports no separate "cache write",
    // so that column is never used for them.
    private static readonly (string Id, ModelPrice Price)[] Table =
    {
        ("claude-opus-5-5",   new(4m,    5m,     0.20m, 20m)),
        ("claude-sonnet-5-5", new(2m,    2.50m,  0.10m, 10m)),
        ("claude-haiku-5-5",  new(0.10m, 0.125m, 0.01m, 0.50m)),   // for prompts up to 100,000 tokens, which the app never exceeds
        ("claude-haiku-4-5",  new(1m,    1.25m,  0.10m, 5m)),

        ("gpt-6-astra",       new(10m,   10m,    1m,    50m)),      // up to 272K tokens of prompt, which the app never exceeds
        ("gpt-6.1-sol",       new(2m,    2m,     0.10m, 10m)),
        ("gpt-6-luna",        new(0.10m, 0.10m,  0.01m, 0.50m)),

        // Gemini 3.8 Flash costs $0.75 / $3.75 until the end of 2026; the standard price from 2027 is used, so the estimate
        // is never too low. Google's cached-input rate wasn't confirmed, so cached tokens are counted at the full price.
        ("gemini-3.8-flash",  new(1.50m, 1.50m,  1.50m, 7.50m)),
        ("gemini-3.1-pro",    new(2m,    2m,     2m,    12m)),      // prompts up to 200K tokens

        ("grok-4.7",          new(2m,    2m,     0.50m, 6m)),      // up to 200K tokens of prompt
        ("grok-4.3",          new(1.25m, 1.25m,  0.20m, 2.50m)),
    };

    /// <summary>The price for a model id (a dated id like <c>claude-haiku-4-5-20251001</c> counts as its base id).</summary>
    public static bool TryGet(string? model, out ModelPrice price)
    {
        foreach (var (id, p) in Table)
        {
            if (model is not null && model.StartsWith(id, StringComparison.Ordinal))
            {
                price = p;
                return true;
            }
        }
        price = null!;
        return false;
    }
}

/// <summary>A point-in-time total of what the AI requests of this session used.</summary>
public sealed record UsageSnapshot(int Requests, long Input, long CacheWrite, long CacheRead, long Output, decimal Cost, bool Unpriced)
{
    public long TotalInput => Input + CacheWrite + CacheRead;

    /// <summary>The share of all input tokens that came from the cache (0 to 1).</summary>
    public double CachedShare => TotalInput == 0 ? 0 : (double)CacheRead / TotalInput;
}

/// <summary>Adds up the usage of every request. Safe to call from any thread.</summary>
public sealed class UsageMeter
{
    private readonly object _lock = new();
    private int _requests;
    private long _input, _write, _read, _output;
    private decimal _cost;
    private bool _unpriced;

    public void Add(TokenUsage usage)
    {
        lock (_lock)
        {
            _requests++;
            _input += usage.Input;
            _write += usage.CacheWrite;
            _read += usage.CacheRead;
            _output += usage.Output;
            if (Pricing.TryGet(usage.Model, out var price)) _cost += price.Cost(usage);
            else _unpriced = true; // counted in tokens, but there's no price to turn it into dollars
        }
    }

    public UsageSnapshot Snapshot()
    {
        lock (_lock) return new UsageSnapshot(_requests, _input, _write, _read, _output, _cost, _unpriced);
    }
}
