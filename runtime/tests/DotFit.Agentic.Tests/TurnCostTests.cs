using DotFit.Agents.Cost;
using DotFit.Agentic.Config;
using DotFit.Agentic.Turn;
using DotFit.Agentic.Tools;
using DotFit.Agents.Config;
using Microsoft.Extensions.AI;

namespace DotFit.Agentic.Tests;

/// <summary>
/// The cost accounting behind <c>result.cost</c> (§9) and the turn log's
/// <c>cost</c> block (§10). Deterministic arithmetic over observed counts —
/// exactly the layer design §11 says deserves unit tests, because a wrong
/// price here is invisible everywhere else: every number the owners see flows
/// through it.
/// </summary>
public class PriceSheetTests
{
    private static Dictionary<string, string> Env(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public void Defaults_are_placeholders_and_say_so()
    {
        var sheet = PriceSheet.FromValues(Env());

        Assert.Equal("USD", sheet.Currency);
        Assert.Equal("builtin-2026-09", sheet.Id);
        Assert.Equal(1.25m, sheet.ChatInputPerMillion);
        Assert.Equal(0.125m, sheet.ChatCachedInputPerMillion);
        Assert.Equal(10m, sheet.ChatOutputPerMillion);
        Assert.Equal(0.13m, sheet.EmbeddingPerMillion);
        Assert.Equal(0.25m, sheet.SearchPerThousand);
        // Unset small-chat prices mirror the chat trio's numbers ON PURPOSE:
        // the deployment SMALL_CHAT currently points at is the same model as
        // CHAT, so the chat rate is the honest default for the small tier.
        Assert.Equal(sheet.ChatInputPerMillion, sheet.SmallChatInputPerMillion);
        Assert.Equal(sheet.ChatCachedInputPerMillion, sheet.SmallChatCachedInputPerMillion);
        Assert.Equal(sheet.ChatOutputPerMillion, sheet.SmallChatOutputPerMillion);
    }

    [Fact]
    public void Unset_small_chat_prices_follow_the_configured_chat_trio()
    {
        // The deployment this repo runs has real chat prices configured and
        // no small-chat prices — which priced the small tier at builtin
        // frontier rates until this fallback existed, overstating it ~5x.
        var sheet = PriceSheet.FromValues(Env(
            (PriceSheet.ChatInputVar, "0.25"),
            (PriceSheet.ChatCachedInputVar, "0.02"),
            (PriceSheet.ChatOutputVar, "1.2")));

        Assert.Equal(0.25m, sheet.SmallChatInputPerMillion);
        Assert.Equal(0.02m, sheet.SmallChatCachedInputPerMillion);
        Assert.Equal(1.2m, sheet.SmallChatOutputPerMillion);
    }

    [Fact]
    public void The_env_overrides_reach_the_sheet()
    {
        var sheet = PriceSheet.FromValues(Env(
            (PriceSheet.ChatInputVar, "2.5"),
            (PriceSheet.ChatCachedInputVar, "0.25"),
            (PriceSheet.ChatOutputVar, "15"),
            (PriceSheet.SmallChatInputVar, "0.3"),
            (PriceSheet.SmallChatCachedInputVar, "0.03"),
            (PriceSheet.SmallChatOutputVar, "1.2"),
            (PriceSheet.EmbeddingVar, "0.06"),
            (PriceSheet.SearchVar, "1"),
            (PriceSheet.CurrencyVar, "eur"),
            (PriceSheet.IdVar, "2026-10-live")));

        Assert.Equal(2.5m, sheet.ChatInputPerMillion);
        Assert.Equal(0.25m, sheet.ChatCachedInputPerMillion);
        Assert.Equal(15m, sheet.ChatOutputPerMillion);
        Assert.Equal(0.3m, sheet.SmallChatInputPerMillion);
        Assert.Equal(0.03m, sheet.SmallChatCachedInputPerMillion);
        Assert.Equal(1.2m, sheet.SmallChatOutputPerMillion);
        Assert.Equal(0.06m, sheet.EmbeddingPerMillion);
        Assert.Equal(1m, sheet.SearchPerThousand);
        Assert.Equal("EUR", sheet.Currency);
        Assert.Equal("2026-10-live", sheet.Id);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1,5")]
    public void A_malformed_price_fails_the_load_rather_than_costing_with_it(string value)
    {
        // Same rule as every other env knob: a bad value must kill the boot,
        // not serve confident wrong numbers to the owners.
        EnvFile.EnvFileException error = Assert.Throws<EnvFile.EnvFileException>(() =>
            PriceSheet.FromValues(Env((PriceSheet.ChatOutputVar, value))));

        Assert.Contains(PriceSheet.ChatOutputVar, error.Message, StringComparison.Ordinal);
    }
}

public class TurnMeterTests
{
    /// <summary>A sheet with round numbers, so the arithmetic is the assertion.</summary>
    private static PriceSheet Sheet() => new()
    {
        Id = "test-sheet",
        ChatInputPerMillion = 1m,
        ChatCachedInputPerMillion = 0.1m,
        ChatOutputPerMillion = 10m,
        EmbeddingPerMillion = 0.2m,
        SearchPerThousand = 2m,
    };

    [Fact]
    public void Cached_input_is_priced_at_its_own_rate_and_never_mints_negative_tokens()
    {
        var meter = new TurnMeter();
        meter.Chat(1_000, 400, 10);

        TurnCost cost = meter.Cost(Sheet());

        // 600 uncached at 1, 400 cached at 0.1, 10 output at 10.
        Assert.Equal(0.0006m + 0.00004m + 0.0001m, cost.ChatUsd);
        Assert.Equal(1_000, cost.ChatInputTokens);
        Assert.Equal(400, cost.ChatCachedInputTokens);

        // A provider reporting an inconsistent pair (cached > input) must not
        // produce a negative uncached count — or negative money.
        var odd = new TurnMeter();
        odd.Chat(100, 500, 0);
        TurnCost oddCost = odd.Cost(Sheet());
        Assert.Equal(500 * 0.1m / 1_000_000m, oddCost.ChatUsd);
        Assert.True(oddCost.ChatUsd >= 0);
    }

    [Fact]
    public void The_three_components_sum_to_the_total()
    {
        var meter = new TurnMeter();
        meter.Chat(1_000, 0, 10);
        meter.Embedding(31);
        meter.IndexQuery();
        meter.IndexQuery(2);
        meter.RankerQuery();

        TurnCost cost = meter.Cost(Sheet());

        Assert.Equal(1, cost.EmbeddingCalls);
        Assert.Equal(31, cost.EmbeddingTokens);
        Assert.Equal(3, cost.IndexQueries);
        Assert.Equal(1, cost.RankerQueries);
        Assert.Equal(31 * 0.2m / 1_000_000m, cost.EmbeddingUsd);
        Assert.Equal(3 * 2m / 1_000m, cost.SearchUsd);
        Assert.Equal(cost.ChatUsd + cost.EmbeddingUsd + cost.SearchUsd, cost.TotalUsd);
        Assert.Equal("test-sheet", cost.PriceSheet);
        Assert.Equal("USD", cost.Currency);
    }

    [Fact]
    public void Small_chat_usage_is_priced_at_its_own_tier()
    {
        // v1's guardrail/rewrite/claims/chat-reply run on the small
        // deployment; the agentic runtime never touches it and reports zeros.
        var meter = new TurnMeter();
        meter.ChatSmall(1_000, 400, 10);
        meter.Chat(1_000, 400, 10);

        TurnCost cost = meter.Cost(new PriceSheet
        {
            ChatInputPerMillion = 1m,
            ChatCachedInputPerMillion = 0.1m,
            ChatOutputPerMillion = 10m,
            SmallChatInputPerMillion = 2m,
            SmallChatCachedInputPerMillion = 0.2m,
            SmallChatOutputPerMillion = 20m,
        });

        Assert.Equal(0.0006m + 0.00004m + 0.0001m, cost.ChatUsd);
        // Double rate, same counts — visibly a different tier.
        Assert.Equal(2 * cost.ChatUsd, cost.SmallChatUsd);
        Assert.Equal(1_000, cost.SmallChatInputTokens);
        Assert.Equal(400, cost.SmallChatCachedInputTokens);
    }

    [Fact]
    public void A_turn_that_cost_nothing_says_zero_not_null()
    {
        // Small talk is a real outcome (§6): no tool, no usage — but it still
        // gets a cost block, because "0.0000" and "missing" are different
        // answers to "what did that turn cost".
        var cost = new TurnMeter().Cost(Sheet());

        Assert.Equal(0m, cost.TotalUsd);
        Assert.Equal(0, cost.IndexQueries);
    }

    [Fact]
    public async Task Index_queries_are_counted_per_call_made_by_the_tools()
    {
        // The counting rules, pinned: a served search is 1 (plus 1 for the
        // ranker when it ran), a fetch is 1 plus one per neighbour probe, a
        // get_product filter is 1. A refused budget counts nothing.
        var tools = new KnowledgeTools(
            new FakeSearch(Fixtures.Document(id: "pdsrg-example-001")),
            new FakeDocumentStore(
                Fixtures.Document(id: "pdsrg-example-001"),
                Fixtures.Document(id: "pdsrg-example-002"),
                Fixtures.Document(id: "pdsrg-example-003")),
            Fixtures.Aliases(), new AgenticOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)),
            meter: new TurnMeter());
        var functions = tools.AsTools();

        await ((AIFunction)functions[0]).InvokeAsync(new AIFunctionArguments { ["query"] = "creatine" });
        await ((AIFunction)functions[1]).InvokeAsync(new AIFunctionArguments
        {
            ["id"] = "pdsrg-example-002", ["neighbors"] = true,
        });
        await ((AIFunction)functions[2]).InvokeAsync(new AIFunctionArguments { ["name_or_part_no"] = "ExampleFormula" });

        TurnCost cost = tools.Meter.Cost(Sheet());
        Assert.Equal(1 + 3 + 1, cost.IndexQueries);   // search; fetch + two probes; product filter
        Assert.Equal(0, cost.RankerQueries);          // the ranker is off by default

        // And the budget refusal: no call made, no query counted.
        var spent = new KnowledgeTools(
            new FakeSearch(), new FakeDocumentStore(), Fixtures.Aliases(), new AgenticOptions(),
            new SourceLedger(6000), new ToolBudget(0, TimeSpan.FromMinutes(5)));
        await ((AIFunction)spent.AsTools()[0]).InvokeAsync(new AIFunctionArguments { ["query"] = "creatine" });
        Assert.Equal(0, spent.Meter.Cost(Sheet()).IndexQueries);
    }
}
