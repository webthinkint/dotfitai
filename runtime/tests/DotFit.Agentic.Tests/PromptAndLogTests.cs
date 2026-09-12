using DotFit.Agentic.Config;
using DotFit.Agentic.Prompting;
using DotFit.Agentic.Turn;
using DotFit.Agents.Aliases;
using DotFit.Agents.Config;

namespace DotFit.Agentic.Tests;

/// <summary>
/// The system prompt is this branch's main safety mechanism (design §8), so the
/// things that must be in it are asserted rather than assumed. These are
/// presence checks, not quality checks — no test can tell you the wording works,
/// only that a deletion was noticed.
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

    [Theory]
    [InlineData("pregnan")]
    [InlineData("under 18")]
    [InlineData("self-harm")]
    [InlineData("disordered eating")]
    [InlineData("Prescription medication")]
    public void The_escalation_list_survives_in_the_assembled_prompt(string topic)
    {
        string prompt = SystemPrompt.Build(Fixtures.Aliases());
        Assert.Contains(topic, prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escalation_is_handled_in_the_conversation_rather_than_by_refusing()
    {
        // Decision D5. If this instruction goes, the branch has quietly become
        // v1's refusal behaviour without the gate that at least made it visible.
        string prompt = SystemPrompt.Build(Fixtures.Aliases());
        Assert.Contains("Stay in the conversation", prompt, StringComparison.Ordinal);
        Assert.Contains("do not refuse the whole turn", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_claims_rule_outranks_the_rest_and_says_so()
    {
        string prompt = SystemPrompt.Build(Fixtures.Aliases());
        Assert.Contains("outranks every other instruction", prompt, StringComparison.Ordinal);
        Assert.Contains("diagnoses, treats, cures or prevents", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_support_route_is_configurable_and_droppable()
    {
        string withRoute = SystemPrompt.Build(Fixtures.Aliases(), "support@example.com");
        Assert.Contains("support@example.com", withRoute, StringComparison.Ordinal);

        // DOTFIT_SUPPORT_CONTACT=none — a deployment whose audience is not
        // dotFIT's support line still gets a route, just not that one.
        string without = SystemPrompt.Build(Fixtures.Aliases(), null);
        Assert.DoesNotContain("support@example.com", without, StringComparison.Ordinal);
        Assert.Contains("healthcare professional", without, StringComparison.Ordinal);
    }

    [Fact]
    public void The_citation_contract_tells_the_model_the_numbering_rule()
    {
        // The client-side half of §7 only holds if the model cites the numbers
        // it was given, in the turn it was given them.
        string prompt = SystemPrompt.Build(Fixtures.Aliases());
        Assert.Contains("keeps its number for the whole turn", prompt, StringComparison.Ordinal);
        Assert.Contains("never cite a source from an earlier turn", prompt, StringComparison.Ordinal);
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
        // Enforced by the schema having no field for it (§10), not by
        // remembering. This test is the tripwire on that claim.
        string line = TurnLog.From(Result(Call(Stages.Search, "creatine dosing")), TurnLog.OutcomeAnswered).ToJsonLine();

        Assert.DoesNotContain("Take 5 g daily", line, StringComparison.Ordinal);
        foreach (string forbidden in (string[])["\"question\"", "\"answer\"", "\"answer_text\"", "\"text\""])
            Assert.DoesNotContain(forbidden, line, StringComparison.Ordinal);

        // The one text-bearing field, and it is the model's, not the customer's
        // — open item 3 is whether that distinction is good enough.
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
    public void The_error_line_keeps_the_kind_and_drops_the_message()
    {
        string line = TurnLog.From(Result(), TurnLog.OutcomeError, errorKind: "RequestFailedException").ToJsonLine();

        Assert.Contains("RequestFailedException", line, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"error\"", line, StringComparison.Ordinal);
    }
}

public class AgenticOptionsTests
{
    [Fact]
    public void Defaults_are_the_designs_numbers()
    {
        var options = new AgenticOptions();
        Assert.Equal(8, options.MaxToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(60), options.TurnTimeout);
        Assert.Equal(6, options.DefaultTop);
    }

    [Fact]
    public void The_hard_ceiling_stays_under_the_services_request_timeout()
    {
        // The loop must always lose to itself before it loses to the host.
        Assert.Equal(TimeSpan.FromSeconds(110), new AgenticOptions { TurnTimeout = TimeSpan.FromSeconds(90) }.HardTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), new AgenticOptions { TurnTimeout = TimeSpan.FromSeconds(30) }.HardTimeout);
    }

    [Fact]
    public void Overrides_are_read_from_the_env_and_validated()
    {
        var options = AgenticOptions.FromValues(new Dictionary<string, string>
        {
            [AgenticOptions.MaxToolCallsVar] = "3",
            [AgenticOptions.TurnTimeoutVar] = "20",
            [AgenticOptions.DefaultTopVar] = "10",
        });

        Assert.Equal(3, options.MaxToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(20), options.TurnTimeout);
        Assert.Equal(10, options.DefaultTop);
    }

    [Fact]
    public void A_bad_override_names_the_variable_and_not_the_value()
    {
        EnvFile.EnvFileException error = Assert.Throws<EnvFile.EnvFileException>(() =>
            AgenticOptions.FromValues(new Dictionary<string, string>
            {
                [AgenticOptions.MaxToolCallsVar] = "not-a-number",
            }));

        Assert.Contains(AgenticOptions.MaxToolCallsVar, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_override_leaves_the_default()
    {
        var options = AgenticOptions.FromValues(new Dictionary<string, string>());
        Assert.Equal(new AgenticOptions(), options);
    }
}
