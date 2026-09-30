using DotFit.Assistant.Cost;
using DotFit.Assistant.Config;
using DotFit.Assistant.Prompting;
using DotFit.Assistant.Turn;
using DotFit.Assistant.Aliases;

namespace DotFit.Assistant.Tests;

/// <summary>
/// The generated currency block. The written prompt parts are checked for
/// structure in <see cref="PromptTemplateTests"/>; their wording is not tested.
/// </summary>
public class SystemPromptTests
{
    [Fact]
    public void Currency_facts_keep_renames_and_replacements_apart()
    {
        // The distinction is a product claim about a thing that does not exist
        // if it collapses: a rename is one product, a replacement is two.
        string facts = SystemPrompt.CurrencyFacts(Fixtures.Aliases());

        Assert.Contains("OldExample is now ExampleFormula", facts, StringComparison.Ordinal);
        Assert.Contains("RetiredFormula", facts, StringComparison.Ordinal);
        Assert.Contains("DIFFERENT formula", facts, StringComparison.Ordinal);
        Assert.Contains("not the same product", facts, StringComparison.Ordinal);
        Assert.Contains("GoneFormula", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void Currency_facts_are_ordered_so_the_prompt_is_stable_across_boots()
    {
        AliasTable aliases = Fixtures.Aliases();
        Assert.Equal(SystemPrompt.CurrencyFacts(aliases), SystemPrompt.CurrencyFacts(aliases));
    }

    [Fact]
    public void Currency_facts_use_the_same_line_endings_on_every_platform()
    {
        Assert.DoesNotContain("\r", SystemPrompt.CurrencyFacts(Fixtures.Aliases()), StringComparison.Ordinal);
    }
}

public class TurnLogTests
{
    private static TurnResult Result(params ToolCallRecord[] calls) => new()
    {
        AnswerText = "Take 5 g daily [1].",
        Sources =
        [
            new SourceRef { N = 1, Id = "a", SourceType = "pdsrg", Authority = 2, Title = "Dosing", Quotable = true },
            new SourceRef { N = 2, Id = "b", SourceType = "qa", Authority = 3, Title = "Ask", Quotable = false },
        ],
        ToolCalls = calls,
        FirstDeltaMs = 1200,
        TotalMs = 3400,
        BudgetExhausted = false,
        CitedSources = [1],
        Families = ["ExampleFormula"],
    };

    private static ToolCallRecord Call(string tool, string argument) => new()
    {
        Tool = tool,
        Argument = argument,
        ResultCount = 2,
        NewSourceCount = 2,
        ElapsedMs = 300,
    };

    [Fact]
    public void The_line_carries_no_question_and_no_answer_text()
    {
        // Enforced by the schema having no field for it, not by
        // remembering. This test is the tripwire on that claim.
        string line = TurnLog.From(Result(Call(Stages.Search, "creatine dosing")), TurnLog.OutcomeAnswered).ToJsonLine();

        Assert.DoesNotContain("Take 5 g daily", line, StringComparison.Ordinal);
        foreach (string forbidden in (string[])["\"question\"", "\"answer\"", "\"answer_text\"", "\"text\""])
            Assert.DoesNotContain(forbidden, line, StringComparison.Ordinal);

        // The one text-bearing field, and it is the model's, not the customer's.
        Assert.Contains("\"queries\":[\"creatine dosing\"]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Queries_are_read_off_the_search_calls_only()
    {
        TurnLog log = TurnLog.From(
            Result(Call(Stages.Search, "creatine dosing"), Call(Stages.Product, "CreatineComplex")),
            TurnLog.OutcomeAnswered);

        Assert.Equal(["creatine dosing"], log.Queries);
        Assert.Equal(2, log.ToolCalls);
        Assert.Equal(1, log.ToolsUsed[Stages.Search]);
        Assert.Equal(1, log.ToolsUsed[Stages.Product]);
    }

    [Fact]
    public void Cited_authorities_come_from_the_cited_sources_not_the_retrieved_ones()
    {
        // Source 2 was retrieved and not cited; logging its authority would
        // read as an answer grounded in material it never used.
        TurnLog log = TurnLog.From(Result(Call(Stages.Search, "q")), TurnLog.OutcomeAnswered);

        Assert.Equal([2], log.CitedAuthorities);
        Assert.Equal(2, log.SourceCount);
        Assert.Equal(1, log.CitedCount);
    }

    [Fact]
    public void Families_are_our_vocabulary_and_are_logged()
    {
        TurnLog log = TurnLog.From(Result(), TurnLog.OutcomeAnswered);
        Assert.Equal(["ExampleFormula"], log.Families);
    }

    [Fact]
    public void The_line_carries_the_cost_block_with_its_sheet()
    {
        // The money is only interpretable against the prices that produced it,
        // so the sheet id travels with every cost — a line read months later
        // still says what priced it.
        TurnResult result = Result() with
        {
            Cost = new TurnCost
            {
                Currency = "USD",
                PriceSheet = "test-sheet",
                ChatInputTokens = 3_000,
                ChatCachedInputTokens = 1_400,
                ChatOutputTokens = 30,
                EmbeddingCalls = 1,
                EmbeddingTokens = 31,
                IndexQueries = 1,
                RankerQueries = 0,
                ChatUsd = 0.00204m,
                EmbeddingUsd = 0.0000062m,
                SearchUsd = 0.002m,
            },
        };

        string line = TurnLog.From(result, TurnLog.OutcomeAnswered).ToJsonLine();

        Assert.Contains("\"cost\":{", line, StringComparison.Ordinal);
        Assert.Contains("\"price_sheet\":\"test-sheet\"", line, StringComparison.Ordinal);
        Assert.Contains("\"total_usd\":0.0040462", line, StringComparison.Ordinal);
        Assert.Equal("1.3.0", TurnLog.SchemaVersion);
    }

    [Fact]
    public void Reference_versions_are_logged_only_for_the_references_read()
    {
        // References are uncited, so this field is the only record of which
        // revision of dotFIT's guidance shaped an answer.
        TurnLog without = TurnLog.From(Result(Call(Stages.Search, "q")), TurnLog.OutcomeAnswered);
        Assert.Null(without.References);
        Assert.DoesNotContain("\"references\"", without.ToJsonLine(), StringComparison.Ordinal);

        TurnLog with = TurnLog.From(
            Result(
                Call(Stages.Guide, "program-guide") with { Version = "aaaaaaaaaaaa" },
                Call(Stages.Guide, "customer-service-faq#Returns") with { Version = "bbbbbbbbbbbb" },
                Call(Stages.Guide, "nope")),
            TurnLog.OutcomeAnswered);
        Assert.Equal(
            new Dictionary<string, string> { ["customer-service-faq"] = "bbbbbbbbbbbb", ["program-guide"] = "aaaaaaaaaaaa" },
            with.References);
        Assert.Equal(3, with.ToolsUsed[Stages.Guide]);
    }

    [Fact]
    public void The_prompt_variant_and_version_are_logged_when_known()
    {
        var prompt = new AssembledPrompt("safety-early", "the prompt text");
        string line = TurnLog.From(Result(), TurnLog.OutcomeAnswered, prompt: prompt).ToJsonLine();

        Assert.Contains("\"prompt_variant\":\"safety-early\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"prompt_version\":\"{PromptTemplate.Version("the prompt text")}\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("the prompt text", line, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_variant", TurnLog.From(Result(), TurnLog.OutcomeAnswered).ToJsonLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_error_line_keeps_the_kind_and_drops_the_message()
    {
        string line = TurnLog.From(Result(), TurnLog.OutcomeError, errorKind: "RequestFailedException").ToJsonLine();

        Assert.Contains("RequestFailedException", line, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"error\"", line, StringComparison.Ordinal);
    }
}

public class AssistantOptionsTests
{
    [Fact]
    public void Defaults_are_eight_calls_sixty_seconds_and_six_sources()
    {
        var options = new AssistantOptions();
        Assert.Equal(8, options.MaxToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(60), options.TurnTimeout);
        Assert.Equal(6, options.DefaultTop);
    }

    [Fact]
    public void The_hard_ceiling_stays_under_the_services_request_timeout()
    {
        // The loop must always lose to itself before it loses to the host.
        Assert.Equal(TimeSpan.FromSeconds(110), new AssistantOptions { TurnTimeout = TimeSpan.FromSeconds(90) }.HardTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), new AssistantOptions { TurnTimeout = TimeSpan.FromSeconds(30) }.HardTimeout);
    }

    [Fact]
    public void Overrides_are_read_from_the_env_and_validated()
    {
        var options = AssistantOptions.FromValues(new Dictionary<string, string>
        {
            [AssistantOptions.MaxToolCallsVar] = "3",
            [AssistantOptions.TurnTimeoutVar] = "20",
            [AssistantOptions.DefaultTopVar] = "10",
        });

        Assert.Equal(3, options.MaxToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(20), options.TurnTimeout);
        Assert.Equal(10, options.DefaultTop);
    }

    [Fact]
    public void The_prompt_variant_is_read_from_the_env_and_defaults_to_default()
    {
        Assert.Equal(PromptTemplate.DefaultVariant, AssistantOptions.FromValues(new Dictionary<string, string>()).PromptVariant);
        Assert.Equal("safety-early", AssistantOptions.FromValues(new Dictionary<string, string>
        {
            [AssistantOptions.PromptVariantVar] = " safety-early ",
        }).PromptVariant);
    }

    [Fact]
    public void A_bad_override_names_the_variable_and_not_the_value()
    {
        EnvFile.EnvFileException error = Assert.Throws<EnvFile.EnvFileException>(() =>
            AssistantOptions.FromValues(new Dictionary<string, string>
            {
                [AssistantOptions.MaxToolCallsVar] = "not-a-number",
            }));

        Assert.Contains(AssistantOptions.MaxToolCallsVar, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_override_leaves_the_default()
    {
        var options = AssistantOptions.FromValues(new Dictionary<string, string>());
        Assert.Equal(new AssistantOptions(), options);
    }

    [Fact]
    public void The_defaults_that_ship_are_the_property_initializers()
    {
        // The service and the CLI both load through FromValues, so if it
        // repeated the defaults as literals, changing an initializer would
        // change nothing that ships. Pinned by construction rather than by number.
        var moved = new AssistantOptions { MaxToolCalls = 5, DefaultTop = 4 };
        var loaded = AssistantOptions.FromValues(new Dictionary<string, string>());

        Assert.Equal(new AssistantOptions().MaxToolCalls, loaded.MaxToolCalls);
        Assert.Equal(new AssistantOptions().DefaultTop, loaded.DefaultTop);
        Assert.Equal(new AssistantOptions().TurnTimeout, loaded.TurnTimeout);
        // A sanity check that the comparison above could fail at all.
        Assert.NotEqual(moved.MaxToolCalls, loaded.MaxToolCalls);
    }

    [Fact]
    public void The_accepted_turn_timeout_can_never_invert_the_ceiling()
    {
        // Above 110 the ceiling sat *below* the research budget, so the budget
        // refusal — the one that ends in an answer — could never fire and every
        // long turn ended in the handoff instead.
        EnvFile.EnvFileException error = Assert.Throws<EnvFile.EnvFileException>(() =>
            AssistantOptions.FromValues(new Dictionary<string, string>
            {
                [AssistantOptions.TurnTimeoutVar] = "115",
            }));
        Assert.Contains(AssistantOptions.TurnTimeoutVar, error.Message, StringComparison.Ordinal);

        var highest = AssistantOptions.FromValues(new Dictionary<string, string>
        {
            [AssistantOptions.TurnTimeoutVar] = AssistantOptions.MaxTurnTimeoutSeconds.ToString(),
        });
        Assert.True(highest.TurnTimeout < highest.HardTimeout);
    }
}
