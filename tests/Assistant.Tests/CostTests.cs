using System.Text.Json;
using Assistant.Core;

namespace Assistant.Tests;

public class PricingTests
{
    static decimal Cost(string model, long input = 0, long write = 0, long read = 0, long output = 0)
    {
        Assert.True(Pricing.TryGet(model, out var price), $"no price for {model}");
        return price.Cost(new TokenUsage(model, input, write, read, output));
    }

    [Fact]
    public void Prices_match_the_published_list_per_million_tokens()
    {
        Assert.Equal(2m, Cost("claude-sonnet-5-5", input: 1_000_000));
        Assert.Equal(2.5m, Cost("claude-sonnet-5-5", write: 1_000_000));
        Assert.Equal(0.1m, Cost("claude-sonnet-5-5", read: 1_000_000));
        Assert.Equal(10m, Cost("claude-sonnet-5-5", output: 1_000_000));

        Assert.Equal(4m, Cost("claude-opus-5-5", input: 1_000_000));
        Assert.Equal(0.2m, Cost("claude-opus-5-5", read: 1_000_000));
        Assert.Equal(20m, Cost("claude-opus-5-5", output: 1_000_000));

        Assert.Equal(0.1m, Cost("claude-haiku-5-5", input: 1_000_000));
        Assert.Equal(0.5m, Cost("claude-haiku-5-5", output: 1_000_000));
    }

    [Fact]
    public void A_dated_model_id_is_priced_as_its_base_model()
    {
        Assert.Equal(1m, Cost("claude-haiku-4-5-20251001", input: 1_000_000));
    }

    [Fact]
    public void Every_model_the_app_offers_has_a_price()
    {
        foreach (var m in Models.All) Assert.True(Pricing.TryGet(m.Id, out _), m.Id);
    }

    [Fact]
    public void Sonnet_is_the_default_and_costs_half_of_opus()
    {
        Assert.Equal("claude-sonnet-5-5", Models.Default);
        Assert.Equal(Models.Default, new Settings().Model);
        Pricing.TryGet("claude-opus-5-5", out var opus);
        Pricing.TryGet("claude-sonnet-5-5", out var sonnet);
        Assert.Equal(opus.Input / 2, sonnet.Input);
        Assert.Equal(opus.Output / 2, sonnet.Output);
    }

    [Fact]
    public void A_model_that_is_no_longer_offered_is_replaced_not_forgotten()
    {
        Assert.Equal("claude-haiku-5-5", new Settings { Model = "claude-haiku-4-5" }.Normalized().Model);
        Assert.Equal(Models.Default, new Settings { Model = "gpt-nope" }.Normalized().Model);
        Assert.Equal("claude-opus-5-5", new Settings { Model = "claude-opus-5-5" }.Normalized().Model);
    }

    [Fact]
    public void The_meter_adds_up_requests_and_flags_what_it_could_not_price()
    {
        var meter = new UsageMeter();
        Assert.Equal(0, meter.Snapshot().Requests);
        meter.Add(new TokenUsage("claude-sonnet-5-5", 1_000_000, 0, 0, 0));
        meter.Add(new TokenUsage("claude-sonnet-5-5", 0, 0, 1_000_000, 0));
        var u = meter.Snapshot();
        Assert.Equal(2, u.Requests);
        Assert.Equal(2.1m, u.Cost);
        Assert.False(u.Unpriced);
        Assert.Equal(0.5, u.CachedShare);

        meter.Add(new TokenUsage("claude-future-9", 5, 5, 5, 5));
        u = meter.Snapshot();
        Assert.Equal(3, u.Requests);
        Assert.Equal(2.1m, u.Cost);   // tokens counted, but no price to turn them into dollars
        Assert.True(u.Unpriced);
    }

    [Fact]
    public void The_meter_is_safe_to_use_from_many_threads()
    {
        var meter = new UsageMeter();
        Parallel.For(0, 1_000, _ => meter.Add(new TokenUsage("claude-sonnet-5-5", 10, 0, 0, 1)));
        var u = meter.Snapshot();
        Assert.Equal(1_000, u.Requests);
        Assert.Equal(10_000, u.Input);
        Assert.Equal(1_000, u.Output);
    }
}

public class ConversationWindowTests
{
    static Conversation Talk(int turns, int chars = 100)
    {
        var c = new Conversation();
        for (int i = 0; i < turns; i++)
            c.Add(i % 2 == 0 ? Speaker.Them : Speaker.Me, $"{i:D4} " + new string('x', chars - 5));
        return c;
    }

    [Fact]
    public void A_short_call_is_sent_whole()
    {
        var c = Talk(10);
        var text = c.Window(3_000, 2_000);
        Assert.DoesNotContain("omitted", text);
        Assert.Equal(c.Render(3_000), text);
    }

    [Fact]
    public void The_oldest_turns_go_in_one_jump_and_then_the_start_holds_still()
    {
        var c = new Conversation();
        string? previousStart = null;
        int moves = 0;
        for (int i = 0; i < 400; i++)
        {
            c.Add(i % 2 == 0 ? Speaker.Them : Speaker.Me, $"{i:D4} " + new string('x', 95));
            var text = c.Window(3_000, 2_000);
            Assert.True(text.Length <= 3_000, $"{text.Length} characters at turn {i}");
            var start = text.Split('\n').Take(2).Last();   // line 0 is the "omitted" notice once trimming began
            if (previousStart is not null && start != previousStart) moves++;
            previousStart = start;
        }
        // 400 turns of ~100 characters, trimming about 10 turns each time: about 40 moves. Sliding by one
        // turn per request would move the start about 300 times and defeat the cache.
        Assert.InRange(moves, 1, 60);
    }

    [Fact]
    public void Trimmed_text_says_so_and_keeps_the_newest_turn()
    {
        var c = Talk(100);
        var text = c.Window(3_000, 2_000);
        Assert.StartsWith("[earlier conversation omitted]\n", text);
        Assert.Contains("0099 ", text);
        Assert.DoesNotContain("0000 ", text);
    }

    [Fact]
    public void Clearing_starts_afresh()
    {
        var c = Talk(100);
        c.Window(3_000, 2_000);
        c.Clear();
        c.Add(Speaker.Them, "Hello again.");
        Assert.Equal("Them: Hello again.", c.Window(3_000, 2_000));
    }

    [Fact]
    public void A_long_monologue_keeps_only_its_latest_part_so_the_newest_turn_stays_small()
    {
        var c = new Conversation();
        c.Add(Speaker.Them, string.Join(" ", Enumerable.Range(0, 20_000).Select(i => "w" + i)));
        var text = c.Window(30_000, 20_000);
        Assert.True(text.Length < Conversation.MaxTurnChars + 20, $"{text.Length} characters");
        Assert.EndsWith("w19999", text);
    }

    [Fact]
    public void The_newest_turn_is_kept_even_when_it_alone_is_over_the_limit()
    {
        var c = new Conversation();
        c.Add(Speaker.Me, "short");
        c.Add(Speaker.Them, new string('y', 4_000));
        var text = c.Window(100, 50);
        Assert.EndsWith(new string('y', 4_000), text);
    }
}

public class CacheableBlocksTests
{
    static SuggestionRequest Req(string transcript, Trigger t = Trigger.Speech, byte[]? png = null)
        => new(transcript, new Settings(), t, null, png);

    static string Talk(int from, int to, int chars = 100)
        => string.Join("\n", Enumerable.Range(from, to - from).Select(i => $"{(i % 2 == 0 ? "Them" : "Me")}: {i:D4} " + new string('x', chars - 11)));

    [Fact]
    public void The_text_is_the_same_as_before_it_was_split_into_blocks()
    {
        var text = Prompting.BuildUserText(Req("Them: hi"));
        Assert.Contains("</style_settings>\n\n<conversation_so_far>\nThem: hi\n</conversation_so_far>\n\n<changed>spoken</changed>", text);
    }

    [Fact]
    public void No_speech_yet_is_still_a_valid_message()
    {
        var blocks = Prompting.FrontBlocks(Req(""));
        Assert.All(blocks, b => Assert.False(string.IsNullOrWhiteSpace(b.Text)));
        Assert.Contains("(no speech yet)", Prompting.BuildUserText(Req("")));
    }

    [Fact]
    public void A_short_conversation_has_nothing_to_remember_beyond_the_instructions()
    {
        Assert.DoesNotContain(Prompting.FrontBlocks(Req(Talk(0, 10))), b => b.CacheAfter);
    }

    [Fact]
    public void A_long_conversation_is_remembered_up_to_but_not_including_its_newest_line()
    {
        var transcript = Talk(0, 60);
        var blocks = Prompting.FrontBlocks(Req(transcript));
        Assert.All(blocks, b => Assert.False(string.IsNullOrWhiteSpace(b.Text)));
        var marked = Assert.Single(blocks, b => b.CacheAfter);
        Assert.NotSame(blocks[^1], marked);
        var remembered = string.Concat(blocks.Take(blocks.ToList().IndexOf(marked) + 1).Select(b => b.Text));
        Assert.Contains("0000 ", remembered);
        Assert.DoesNotContain("0059 ", remembered);          // the newest line is never part of what is remembered
        Assert.Contains("0059 ", blocks[^1].Text);
        Assert.Equal(transcript + "\n", string.Concat(blocks.Skip(1).Select(b => b.Text)));
    }

    [Fact]
    public void What_is_remembered_does_not_change_as_the_conversation_grows()
    {
        var before = Prompting.FrontBlocks(Req(Talk(0, 60))).ToList();
        var cachedCount = before.FindIndex(b => b.CacheAfter) + 1;
        Assert.True(cachedCount > 1);

        // More is said, and the newest person keeps talking: the remembered part must be untouched.
        foreach (var grown in new[] { Talk(0, 60) + " and more.", Talk(0, 63), Talk(0, 70) })
        {
            var after = Prompting.FrontBlocks(Req(grown));
            for (int i = 0; i < cachedCount; i++)
                Assert.Equal(before[i].Text, after[i].Text);
        }
    }

    [Fact]
    public void Nothing_that_changes_every_time_is_in_the_remembered_part()
    {
        var png = new byte[] { 1, 2, 3 };
        var a = Req(Talk(0, 60), Trigger.Speech);
        var b = Req(Talk(0, 60), Trigger.Text | Trigger.Forced, png) with { Hint = "shorter" };
        var front = Prompting.FrontBlocks(a).Select(x => x.Text);
        Assert.Equal(front, Prompting.FrontBlocks(b).Select(x => x.Text));
        Assert.DoesNotContain("shorter", string.Concat(Prompting.FrontBlocks(b).Select(x => x.Text)));
        Assert.Contains("shorter", Prompting.TailText(b));
    }

    [Fact]
    public void A_call_at_the_size_limit_stays_well_inside_the_look_back_window()
    {
        var c = new Conversation();
        var o = new EngineOptions();
        for (int i = 0; i < 1_000; i++) c.Add(i % 2 == 0 ? Speaker.Them : Speaker.Me, $"Turn number {i} with a sentence of ordinary speech in it, about this long.");
        var blocks = Prompting.FrontBlocks(Req(c.Window(o.TranscriptChars, o.TranscriptKeepChars)));
        Assert.True(blocks.Count < 20, $"{blocks.Count} blocks");
        Assert.Single(blocks, b => b.CacheAfter);
    }
}

public class RequestCostShapeTests
{
    static SuggestionRequest Req(string model, string transcript, byte[]? png = null, Action<TokenUsage>? onUsage = null)
        => new(transcript, new Settings { Model = model }, png is null ? Trigger.Speech : Trigger.Speech | Trigger.Text, null, png) { OnUsage = onUsage };

    static bool HasMarker(JsonElement block) =>
        block.TryGetProperty("cache_control", out var c) && c.ValueKind == JsonValueKind.Object;

    static string LongTalk() => string.Join("\n", Enumerable.Range(0, 80).Select(i => $"{(i % 2 == 0 ? "Them" : "Me")}: {i:D4} " + new string('x', 100)));

    static async Task Drain(ClaudeSuggester s, SuggestionRequest r)
    {
        await foreach (var _ in s.StreamAsync(r, CancellationToken.None)) { }
    }

    [Fact]
    public async Task The_instructions_and_the_older_conversation_are_marked_for_claude_to_remember()
    {
        using var server = new FakeClaudeServer();
        await Drain(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req("claude-sonnet-5-5", LongTalk(), png: new byte[] { 0x89 }));

        var (_, body, _) = Assert.Single(server.Requests);
        var content = body.GetProperty("messages")[0].GetProperty("content");
        var marked = Enumerable.Range(0, content.GetArrayLength())
            .Where(i => HasMarker(content[i])).ToList();
        var markers = marked.Count + 1;   // plus the one on the instructions
        Assert.InRange(markers, 2, 4);
        Assert.Single(marked);

        var last = content.GetArrayLength() - 1;
        Assert.True(marked[0] < last - 1, "the marker must come before the picture and the task");
        Assert.DoesNotContain("0079 ", content[marked[0]].GetProperty("text").GetString());   // the newest line is never remembered
        Assert.False(content[last].TryGetProperty("cache_control", out _));       // not even sent as null
        Assert.False(content[last - 1].TryGetProperty("cache_control", out _));
        Assert.All(Enumerable.Range(0, last + 1).Except(marked), i => Assert.False(content[i].TryGetProperty("cache_control", out _)));
        Assert.Equal("image", content[last - 1].GetProperty("type").GetString());
        Assert.True(content.GetArrayLength() <= 20);
    }

    [Fact]
    public async Task Two_requests_in_a_row_start_with_the_same_remembered_text()
    {
        using var server = new FakeClaudeServer();
        var s = new ClaudeSuggester(() => "k", baseUrl: server.Url);
        var talk = LongTalk();
        await Drain(s, Req("claude-sonnet-5-5", talk));
        await Drain(s, Req("claude-sonnet-5-5", talk + "\nThem: 0080 and one more thing for you.", new byte[] { 0x89 }));

        string Remembered(JsonElement body)
        {
            var content = body.GetProperty("messages")[0].GetProperty("content");
            int end = Enumerable.Range(0, content.GetArrayLength()).First(i => HasMarker(content[i]));
            return string.Concat(Enumerable.Range(0, end + 1).Select(i => content[i].GetProperty("text").GetString()));
        }
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(Remembered(server.Requests[0].Body), Remembered(server.Requests[1].Body));
        Assert.Equal(server.Requests[0].Body.GetProperty("system").GetRawText(), server.Requests[1].Body.GetProperty("system").GetRawText());
    }

    [Fact]
    public async Task Haiku_5_5_takes_the_low_effort_setting_but_not_the_fallback_option()
    {
        using var server = new FakeClaudeServer();
        await Drain(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req("claude-haiku-5-5", "Them: hello there friend"));
        var (_, body, headers) = Assert.Single(server.Requests);
        Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(body.TryGetProperty("fallbacks", out _));
        Assert.False(headers.TryGetValue("anthropic-beta", out var beta) && beta!.Contains("fallback"));
    }

    [Fact]
    public async Task What_a_request_used_is_reported_when_it_finishes()
    {
        using var server = new FakeClaudeServer { InputTokens = 40, CacheWritten = 300, CacheRead = 2_000, OutputTokens = 55 };
        TokenUsage? seen = null;
        await Drain(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req("claude-sonnet-5-5", LongTalk(), onUsage: u => seen = u));
        Assert.Equal(new TokenUsage("claude-sonnet-5-5", 40, 300, 2_000, 55), seen);
    }

    [Fact]
    public async Task What_a_request_used_is_still_reported_when_it_is_cut_short()
    {
        using var server = new FakeClaudeServer { InputTokens = 40, CacheRead = 2_000 };
        var reports = new List<TokenUsage>();
        var s = new ClaudeSuggester(() => "k", baseUrl: server.Url);
        await foreach (var _ in s.StreamAsync(Req("claude-sonnet-5-5", LongTalk(), onUsage: reports.Add), CancellationToken.None))
            break;   // a newer request replaced this one after the first words
        var u = Assert.Single(reports);
        Assert.Equal((40L, 2_000L), (u.Input, u.CacheRead));
        Assert.True(u.Output >= 1);
    }

    [Fact]
    public async Task A_failed_request_reports_nothing()
    {
        using var server = new FakeClaudeServer { Status = 401 };
        var reports = new List<TokenUsage>();
        await Assert.ThrowsAnyAsync<Exception>(() => Drain(new ClaudeSuggester(() => "k", baseUrl: server.Url), Req("claude-sonnet-5-5", "Them: hello", onUsage: reports.Add)));
        Assert.Empty(reports);
    }
}
