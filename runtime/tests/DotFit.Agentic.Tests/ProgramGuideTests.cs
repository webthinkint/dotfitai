using System.Text.RegularExpressions;
using DotFit.Agentic.Config;
using DotFit.Agentic.Prompting;
using DotFit.Agentic.Tools;
using DotFit.Agentic.Turn;
using DotFit.Agents.Aliases;
using Microsoft.Extensions.AI;

namespace DotFit.Agentic.Tests;

/// <summary>
/// The supplement program guide and its tool (design §7.4). The guide's
/// content is dotFIT's to review (open item 12), so nothing here asserts on
/// its advice — only that it ships, that the tool hands it over without
/// making it a source, and that the products it names still exist.
/// </summary>
public partial class ProgramGuideTests
{
    private static KnowledgeTools Tools(ToolBudget? budget = null) => new(
        new FakeSearch(), new FakeDocumentStore(), Fixtures.Aliases(), new AgenticOptions(),
        new SourceLedger(6000), budget ?? new ToolBudget(8, TimeSpan.FromMinutes(5)));

    private static AIFunction GuideFunction(KnowledgeTools tools) =>
        (AIFunction)tools.AsTools().Single(t => t.Name == "get_program_guide");

    [Fact]
    public void The_guide_is_embedded_and_its_version_is_a_stable_hash()
    {
        Assert.True(ProgramGuide.Text.Length > 1000, "the embedded guide is missing or truncated");
        Assert.Matches("^[0-9a-f]{12}$", ProgramGuide.Version);
        Assert.Equal(ProgramGuide.Version, ProgramGuide.Version);
    }

    [Fact]
    public async Task The_tool_returns_the_guide_as_method_not_as_a_numbered_source()
    {
        // Owner-ruled 2026-09-17: uncited. A ledger entry would emit a
        // `source` frame the answer never cites.
        KnowledgeTools tools = Tools();

        string result = (await GuideFunction(tools).InvokeAsync(new AIFunctionArguments()))?.ToString() ?? "";

        Assert.Contains(ProgramGuide.Text.Trim(), result, StringComparison.Ordinal);
        Assert.Contains("do not cite it", result, StringComparison.Ordinal);
        Assert.Empty(tools.Ledger.Sources);
        Assert.DoesNotContain(tools.Ledger.Drain(), e => e is TurnSourceEvent);

        ToolCallRecord call = Assert.Single(tools.Calls);
        Assert.Equal(Stages.Guide, call.Tool);
        Assert.Null(call.Refusal);
    }

    [Fact]
    public async Task The_tool_announces_itself_as_a_stage()
    {
        KnowledgeTools tools = Tools();
        await GuideFunction(tools).InvokeAsync(new AIFunctionArguments());

        Assert.Contains(tools.Ledger.Drain(), e => e is TurnStageEvent { Stage: Stages.Guide });
    }

    [Fact]
    public async Task The_tool_spends_the_turns_budget_like_any_other()
    {
        var budget = new ToolBudget(maxCalls: 1, TimeSpan.FromMinutes(5));
        KnowledgeTools tools = Tools(budget);
        AIFunction guide = GuideFunction(tools);

        await guide.InvokeAsync(new AIFunctionArguments());
        string second = (await guide.InvokeAsync(new AIFunctionArguments()))?.ToString() ?? "";

        Assert.Contains("Do not call any more tools", second, StringComparison.Ordinal);
        Assert.DoesNotContain(ProgramGuide.Text.Trim(), second, StringComparison.Ordinal);
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void Every_part_number_the_guide_names_is_a_current_product()
    {
        // The guide names products by bracketed part number, and the model
        // will recommend them. A number the alias table does not know is a
        // discontinued or mistyped SKU reaching a customer as a recommendation.
        AliasTable aliases = AliasTable.Load(AliasTablePath());
        var known = aliases.Families.SelectMany(f => f.PartNos).ToHashSet();

        int[] named = [.. BracketRegex().Matches(ProgramGuide.Text)
            .SelectMany(m => PartNoRegex().Matches(m.Groups[1].Value))
            .Select(m => int.Parse(m.Value))
            .Distinct()];

        Assert.NotEmpty(named);
        Assert.All(named, partNo => Assert.Contains(partNo, known));
    }

    private static string AliasTablePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "processed", "aliases", "alias_table.json");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("processed/aliases/alias_table.json not found from the test output directory");
    }

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex PartNoRegex();
}
