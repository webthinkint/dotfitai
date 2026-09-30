using System.Text.RegularExpressions;
using DotFit.Assistant.Aliases;
using DotFit.Assistant.Config;
using DotFit.Assistant.Sources;
using DotFit.Assistant.Tools;
using DotFit.Assistant.Turn;
using Microsoft.Extensions.AI;

namespace DotFit.Assistant.Tests;

/// <summary>
/// The source registry, reference sections, and <c>read_reference</c>. The
/// repo's own registry is checked for what must hold for any content; the
/// rules themselves are pinned on synthetic registries.
/// </summary>
public partial class ReferenceTests
{
    private const string Corpora = """
        corpora:
          product: { name: approved product copy, about: product pages }
          infopage: { name: website pages, about: site copy }
          pdsrg: { name: the practitioner guide, about: dosing }
          qa: { name: customer Q&A, about: answers }
          podcast: { name: the podcast, about: discussion }
          menu_desc: { name: menu descriptions, about: menus }
        """;

    private const string Doc = """
        # Handbook

        <!-- authoring note -->
        Intro.

        ## Returns (slides 4-5)

        Return within 30 days.

        ### Damaged items

        Call us.

        ## Shipping

        Free over $80.
        """;

    // ---- the repo's registry -------------------------------------------------

    [Fact]
    public void The_repo_registry_loads_and_every_reference_has_sections()
    {
        SourceRegistry registry = RepoRegistry();

        Assert.NotEmpty(registry.References);
        Assert.All(registry.References, r => Assert.NotEmpty(r.Sections));
    }

    [Fact]
    public void Every_part_number_a_reference_names_is_a_current_product()
    {
        // References name products by bracketed part number, and the model
        // recommends them. A number the alias table does not know is a
        // discontinued or mistyped product reaching a customer.
        var known = AliasTable.Load(AliasTablePath()).Families.SelectMany(f => f.PartNos).ToHashSet();

        foreach (Reference reference in RepoRegistry().References)
        {
            int[] named = [.. BracketRegex().Matches(reference.Text)
                .SelectMany(m => PartNoRegex().Matches(m.Groups[1].Value))
                .Select(m => int.Parse(m.Value))
                .Distinct()];
            Assert.All(named, partNo => Assert.True(known.Contains(partNo), $"{reference.Id} names unknown part {partNo}"));
        }
    }

    // ---- registry rules ------------------------------------------------------

    [Fact]
    public void Corpora_must_match_the_index_source_types()
    {
        using var dir = new TempDir("registry");
        string yaml = Corpora.Replace("  menu_desc: { name: menu descriptions, about: menus }", "");

        var error = Assert.Throws<SourceRegistryException>(() => SourceRegistry.Parse(yaml, dir.Path));
        Assert.Contains("menu_desc", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_reference_file_is_named()
    {
        using var dir = new TempDir("registry");
        string yaml = Corpora + "\nreferences:\n  faq: { file: faq.md, title: FAQ, use_when: questions }\n";

        var error = Assert.Throws<SourceRegistryException>(() => SourceRegistry.Parse(yaml, dir.Path));
        Assert.Contains("faq.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_authority_order_may_only_name_known_sources()
    {
        using var dir = new TempDir("registry");
        string yaml = Corpora + "\nauthority:\n  claims: [product, brochure]\n";

        var error = Assert.Throws<SourceRegistryException>(() => SourceRegistry.Parse(yaml, dir.Path));
        Assert.Contains("brochure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_figures_and_display_names_are_read()
    {
        SourceRegistry registry = Registry(policyFigures: true);

        Assert.True(registry.References.Single().PolicyFigures);
        Assert.Equal("Handbook", registry.DisplayName("handbook"));
        Assert.Equal("approved product copy", registry.DisplayName("product"));
    }

    // ---- sections --------------------------------------------------------------

    [Fact]
    public void Sections_list_the_shallowest_repeated_heading_level_without_slide_notes()
    {
        Reference reference = Registry().References.Single();

        Assert.Equal(["Returns", "Shipping"], reference.Sections);
        Assert.DoesNotContain("authoring note", reference.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_runs_to_the_next_heading_at_its_level_and_matches_loosely()
    {
        Reference reference = Registry().References.Single();

        string returns = reference.Section("returns")!;
        Assert.Contains("Return within 30 days.", returns, StringComparison.Ordinal);
        Assert.Contains("Call us.", returns, StringComparison.Ordinal);
        Assert.DoesNotContain("Free over", returns, StringComparison.Ordinal);

        Assert.Equal("### Damaged items\n\nCall us.", reference.Section("damaged"));
        Assert.Null(reference.Section("warranty"));
    }

    // ---- the tool --------------------------------------------------------------

    [Fact]
    public void Read_reference_is_offered_only_with_a_registry_that_has_references()
    {
        Assert.DoesNotContain(Tools(registry: null).AsTools(), t => t.Name == "read_reference");
        Assert.Contains(Tools(registry: Registry()).AsTools(), t => t.Name == "read_reference");
    }

    [Fact]
    public async Task A_reference_is_guidance_not_a_numbered_source()
    {
        KnowledgeTools tools = Tools(registry: Registry(policyFigures: true));

        string result = await Read(tools, "handbook");

        Assert.Contains("Free over $80.", result, StringComparison.Ordinal);
        Assert.Contains("do not cite it", result, StringComparison.Ordinal);
        Assert.Contains("fixed dotFIT policy", result, StringComparison.Ordinal);
        Assert.Empty(tools.Ledger.Sources);
        Assert.Contains(tools.Ledger.Drain(), e => e is TurnStageEvent { Stage: Stages.Guide, Detail: "Handbook" });

        ToolCallRecord call = Assert.Single(tools.Calls);
        Assert.Equal(Stages.Guide, call.Tool);
        Assert.Equal(Registry().References.Single().Version, call.Version);
        Assert.Null(call.Refusal);
    }

    [Fact]
    public async Task A_section_read_returns_only_that_section()
    {
        KnowledgeTools tools = Tools(registry: Registry());

        string result = await Read(tools, "handbook", "shipping");

        Assert.Contains("Free over $80.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Return within", result, StringComparison.Ordinal);
        Assert.Equal("handbook#shipping", tools.Calls.Single().Argument);
    }

    [Fact]
    public async Task Unknown_ids_and_sections_are_answered_with_what_exists()
    {
        KnowledgeTools tools = Tools(registry: Registry());

        Assert.Contains("handbook", await Read(tools, "manual"), StringComparison.Ordinal);
        string section = await Read(tools, "handbook", "warranty");
        Assert.Contains("Returns; Shipping", section, StringComparison.Ordinal);
        Assert.All(tools.Calls, c => Assert.Null(c.Version));
    }

    [Fact]
    public async Task Reading_a_reference_spends_the_turns_budget()
    {
        var budget = new ToolBudget(maxCalls: 1, TimeSpan.FromMinutes(5));
        KnowledgeTools tools = Tools(budget, Registry());

        await Read(tools, "handbook");
        string second = await Read(tools, "handbook");

        Assert.Contains("Do not call any more tools", second, StringComparison.Ordinal);
        Assert.True(budget.Exhausted);
    }

    // ---- helpers ---------------------------------------------------------------

    private static SourceRegistry Registry(bool policyFigures = false)
    {
        var dir = new TempDir("registry");
        File.WriteAllText(Path.Combine(dir.Path, "handbook.md"), Doc);
        string yaml = Corpora +
            "\nreferences:\n  handbook:\n    file: handbook.md\n    title: Handbook\n    use_when: questions\n" +
            (policyFigures ? "    policy_figures: true\n" : "") +
            "authority:\n  policy: [handbook, infopage]\n";
        // Parsed eagerly, so the folder can go when the fixture does.
        SourceRegistry registry = SourceRegistry.Parse(yaml, dir.Path);
        dir.Dispose();
        return registry;
    }

    private static KnowledgeTools Tools(ToolBudget? budget = null, SourceRegistry? registry = null) => new(
        new FakeSearch(), new FakeDocumentStore(), Fixtures.Aliases(), new AssistantOptions(),
        new SourceLedger(6000), budget ?? new ToolBudget(8, TimeSpan.FromMinutes(5)), registry: registry);

    private static async Task<string> Read(KnowledgeTools tools, string id, string? section = null)
    {
        var function = (AIFunction)tools.AsTools().Single(t => t.Name == "read_reference");
        var args = new AIFunctionArguments { ["id"] = id };
        if (section is not null)
            args["section"] = section;
        return (await function.InvokeAsync(args))?.ToString() ?? "";
    }

    internal static SourceRegistry RepoRegistry() =>
        SourceRegistry.Load(Path.Combine(Path.GetDirectoryName(PromptTemplateTests.RepoPromptDir())!, SourceRegistry.FileName));

    private static string AliasTablePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "pipeline-output", "aliases", "alias_table.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("pipeline-output/aliases/alias_table.json not found from the test output directory");
    }

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex PartNoRegex();
}
