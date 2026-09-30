using DotFit.Assistant.Config;
using DotFit.Assistant.Retrieval;
using DotFit.Assistant.Tools;
using DotFit.Assistant.Turn;
using DotFit.Assistant.Aliases;
using Microsoft.Extensions.AI;

namespace DotFit.Assistant.Tests;

/// <summary>
/// The deterministic half of the assistant: numbering, filters,
/// alias tiers, budgets. This is the layer with a right answer, so it is the
/// layer that gets pinned.
/// </summary>
public class SourceLedgerTests
{
    [Fact]
    public void Numbers_keep_counting_across_calls()
    {
        var ledger = new SourceLedger(1000);
        Assert.Equal(1, ledger.Add(Fixtures.Document(id: "a")).Source.N);
        Assert.Equal(2, ledger.Add(Fixtures.Document(id: "b")).Source.N);
        Assert.Equal(3, ledger.Add(Fixtures.Document(id: "c")).Source.N);
    }

    [Fact]
    public void A_document_keeps_its_number_for_the_whole_turn()
    {
        var ledger = new SourceLedger(1000);
        (SourceRef first, bool firstIsNew) = ledger.Add(Fixtures.Document(id: "a"));
        ledger.Add(Fixtures.Document(id: "b"));
        (SourceRef again, bool againIsNew) = ledger.Add(Fixtures.Document(id: "a"));

        Assert.True(firstIsNew);
        Assert.False(againIsNew);
        Assert.Equal(first.N, again.N);
        Assert.Equal(2, ledger.Sources.Count);
    }

    [Fact]
    public void A_source_event_is_queued_when_the_number_is_assigned()
    {
        // The ordering contract: the event exists before any delta could
        // cite it. A repeat hit queues nothing — the client already has it.
        var ledger = new SourceLedger(1000);
        ledger.Add(Fixtures.Document(id: "a"));
        ledger.Add(Fixtures.Document(id: "a"));

        IReadOnlyList<TurnEvent> drained = ledger.Drain();
        TurnSourceEvent single = Assert.IsType<TurnSourceEvent>(Assert.Single(drained));
        Assert.Equal(1, single.Source.N);
        Assert.Empty(ledger.Drain());
    }

    [Fact]
    public void A_source_carries_the_documents_part_nos()
    {
        // `products` tags copied onto the caller-facing source at
        // numbering time — deterministic catalog metadata, never model text.
        // Pinned on the queued event because that is what reaches the wire.
        var ledger = new SourceLedger(1000);
        ledger.Add(Fixtures.Document(id: "a", products: ["9001", "9002"]));
        ledger.Add(Fixtures.Document(id: "b"));

        TurnSourceEvent queued = Assert.IsType<TurnSourceEvent>(ledger.Drain()[0]);
        Assert.Equal(["9001", "9002"], queued.Source.PartNos);
        Assert.Equal([], ledger.Sources[1].PartNos);
    }

    [Fact]
    public void Stage_events_drain_in_the_order_they_were_queued()
    {
        var ledger = new SourceLedger(1000);
        ledger.Stage(Stages.Search, "creatine dosing");
        ledger.Add(Fixtures.Document(id: "a"));

        IReadOnlyList<TurnEvent> drained = ledger.Drain();
        Assert.Equal(Stages.Search, Assert.IsType<TurnStageEvent>(drained[0]).Stage);
        Assert.IsType<TurnSourceEvent>(drained[1]);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void The_claims_line_sits_between_authority_2_and_3(int authority, bool quotable)
    {
        // line, pinned here because nothing downstream
        // re-checks it: the marker the model reads is the only thing carrying it.
        var ledger = new SourceLedger(1000);
        RetrievedDocument document = Fixtures.Document(authority: authority);
        (SourceRef source, _) = ledger.Add(document);

        Assert.Equal(quotable, source.Quotable);
        string rendered = ledger.Render(source, document, isNew: true);
        Assert.Contains(quotable ? "QUOTABLE FOR PRODUCT CLAIMS" : "CONTEXT ONLY", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_says_so_and_names_the_fetch_that_fixes_it()
    {
        var ledger = new SourceLedger(maxSourceChars: 20);
        RetrievedDocument document = Fixtures.Document(id: "pdsrg-long-004", content: new string('x', 500));
        (SourceRef source, _) = ledger.Add(document);

        string rendered = ledger.Render(source, document, isNew: true);
        Assert.Contains("truncated", rendered, StringComparison.Ordinal);
        Assert.Contains("fetch(\"pdsrg-long-004\")", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        // content[..max] cutting between the halves of an astral character
        // hands the model a lone surrogate, which is not text.
        var ledger = new SourceLedger(maxSourceChars: 5);
        RetrievedDocument document = Fixtures.Document(content: "abcd\U0001F600efgh");
        (SourceRef source, _) = ledger.Add(document);

        string rendered = ledger.Render(source, document, isNew: true);
        Assert.All(rendered, ch => Assert.False(char.IsSurrogate(ch)));
    }

    [Fact]
    public void Cited_reads_the_delivered_text_not_the_sources_given()
    {
        var ledger = new SourceLedger(1000);
        ledger.Add(Fixtures.Document(id: "a"));
        ledger.Add(Fixtures.Document(id: "b"));
        ledger.Add(Fixtures.Document(id: "c"));

        Assert.Equal([1, 3], ledger.CitedIn("Take it with food [1]. It mixes well [3]."));
        Assert.Empty(ledger.CitedIn("No citations here."));
    }

    [Fact]
    public async Task Queued_completes_on_the_enqueue_and_re_arms_after_the_drain()
    {
        // What lets the loop wake on a tool's stage line instead of on the
        // model's next update. Re-arming matters as much as
        // completing: a signal left completed after a drain would spin the loop.
        var ledger = new SourceLedger(1000);

        Task waiting = ledger.Queued;
        Assert.False(waiting.IsCompleted);

        ledger.Stage(Stages.Search, "creatine dosing");
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(ledger.Drain());
        Assert.False(ledger.Queued.IsCompleted);

        // Already queued when asked: a caller that arrives after the enqueue
        // must not have to wait for the next one.
        ledger.Add(Fixtures.Document(id: "pdsrg-example-009"));
        Assert.True(ledger.Queued.IsCompleted);
    }

    [Fact]
    public void Sections_of_one_product_page_share_a_number_and_one_source_event()
    {
        // Product copy numbered per section would put dozens of "[n]"s for one
        // page in a turn, and the model would cite the wrong ones. A page is one source.
        var ledger = new SourceLedger(1000);
        (SourceRef a, bool aIsNew) = ledger.Add(Section("product-9001-a", "https://example.com/p/9001"));
        (SourceRef b, bool bIsNew) = ledger.Add(Section("product-9001-b", "https://example.com/p/9001"));
        (SourceRef c, _) = ledger.Add(Section("product-9002-a", "https://example.com/p/9002"));

        Assert.True(aIsNew);
        Assert.False(bIsNew);
        Assert.Equal(a.N, b.N);
        Assert.Equal(2, c.N);
        Assert.Equal(2, ledger.Drain().OfType<TurnSourceEvent>().Count());
        Assert.NotNull(ledger.DocumentFor("product-9001-b"));
    }

    [Fact]
    public void Only_product_copy_is_grouped_by_page()
    {
        // Every other type keeps one number per document, shared URL or not —
        // two Q&A answers are two sources even when they link the same page.
        var ledger = new SourceLedger(1000);
        ledger.Add(Fixtures.Document(id: "qa-aaaaaaaaaaaaaaaa", sourceType: "qa", authority: 3));
        ledger.Add(Fixtures.Document(id: "qa-bbbbbbbbbbbbbbbb", sourceType: "qa", authority: 3));
        Assert.Equal(2, ledger.Sources.Count);
    }

    [Fact]
    public void A_page_renders_its_header_once_and_every_section_under_its_own_id()
    {
        var ledger = new SourceLedger(10);
        RetrievedDocument[] sections =
        [
            Section("product-9001-a", "https://example.com/p/9001", "Short."),
            Section("product-9001-b", "https://example.com/p/9001", "A section longer than ten characters."),
        ];
        (SourceRef source, bool isNew) = ledger.Add(sections[0]);
        string text = ledger.RenderPage(source, sections, isNew);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"^\[1\] ", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.Contains("section id: product-9001-a", text, StringComparison.Ordinal);
        Assert.Contains("section id: product-9001-b", text, StringComparison.Ordinal);
        Assert.Contains("fetch(\"product-9001-b\")", text, StringComparison.Ordinal);
    }

    internal static RetrievedDocument Section(string id, string url, string content = "Example copy.") =>
        Fixtures.Document(id: id, sourceType: "product", authority: 1, title: "ExampleFormula",
            content: content, products: [id.Split('-')[1]], citationUrl: url);
}

public class FilterTests
{
    [Fact]
    public void Odata_literals_escape_the_quote()
    {
        Assert.Equal("'it''s'", Filters.Quote("it's"));
    }

    [Fact]
    public void Any_product_matches_any_of_the_part_numbers()
    {
        Assert.Equal("products/any(p: p eq '1' or p eq '2')", Filters.AnyProduct(["1", "2"]));
        Assert.Equal("", Filters.AnyProduct([]));
    }

    [Fact]
    public void And_parenthesizes_every_clause_and_drops_the_empty_ones()
    {
        // Unparenthesized, a clause containing a top-level `or` would widen the
        // query past the filters that precede it.
        Assert.Equal("(a eq 1) and (b eq 2 or c eq 3)", Filters.And("a eq 1", "", null, "b eq 2 or c eq 3"));
        Assert.Equal("", Filters.And(null, ""));
    }
}

public class ToolBudgetTests
{
    [Fact]
    public void The_call_budget_refuses_in_prose_rather_than_throwing()
    {
        var budget = new ToolBudget(maxCalls: 2, TimeSpan.FromMinutes(5));
        Assert.True(budget.TryConsume(out _));
        Assert.True(budget.TryConsume(out _));
        Assert.False(budget.TryConsume(out string refusal));

        Assert.True(budget.Exhausted);
        Assert.Contains("Answer now", refusal, StringComparison.Ordinal);
        Assert.Contains("2", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_clock_stops_tool_use_before_the_count_does()
    {
        var budget = new ToolBudget(maxCalls: 99, TimeSpan.Zero);
        Assert.False(budget.TryConsume(out string refusal));
        Assert.Contains("research budget", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_call_is_still_a_call_on_both_branches()
    {
        // If the time branch returned before the counter moved, a model that
        // kept calling tools after the research budget expired would get a free
        // instant refusal each time — and each refusal is a full paid model
        // round trip carrying the whole conversation. The call budget is what
        // stops a spinner; it cannot if the spinning does not count.
        var expired = new ToolBudget(maxCalls: 3, TimeSpan.Zero);
        for (int i = 0; i < 10; i++)
            Assert.False(expired.TryConsume(out _));
        Assert.Equal(10, expired.Used);

        // And the count still wins once it is the binding limit.
        var counted = new ToolBudget(maxCalls: 1, TimeSpan.FromMinutes(5));
        Assert.True(counted.TryConsume(out _));
        Assert.False(counted.TryConsume(out string refusal));
        Assert.Contains("all 1 of its tool calls", refusal, StringComparison.Ordinal);
        Assert.Equal(2, counted.Used);
    }
}

public class DocumentStoreTests
{
    [Fact]
    public void A_key_lookup_that_lands_on_a_superseded_document_returns_nothing()
    {
        // `fetch` is a key lookup, so it carries no filter and could be the one
        // path past `is_current` — while being the tool that numbers what it
        // gets as a citable source. The index holds only current documents
        // today; this pins the invariant regardless.
        static AzureDocumentStore.KbDoc Doc(bool isCurrent) => new()
        {
            Id = "pdsrg-example-004", SourceType = "pdsrg", Authority = 2,
            Title = "Old Section", Content = "superseded body", IsCurrent = isCurrent,
        };

        Assert.Null(AzureDocumentStore.CurrentOnly(Doc(isCurrent: false)));
        Assert.NotNull(AzureDocumentStore.CurrentOnly(Doc(isCurrent: true)));
    }
}

public class AliasTierTests
{
    private static KnowledgeTools Tools(
        out FakeSearch search, out FakeDocumentStore store,
        AssistantOptions? options = null, SearchSettings? settings = null)
    {
        search = new FakeSearch(Fixtures.Document());
        store = new FakeDocumentStore(
            Fixtures.Document(id: "product-9001-description", sourceType: "product", authority: 1,
                products: ["9001"]));
        return new KnowledgeTools(
            search, store, Fixtures.Aliases(), options ?? new AssistantOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)), settings);
    }

    [Fact]
    public void Expansion_terms_are_appended_not_substituted()
    {
        // Replacing the customer's token would lose the very word that makes a
        // rename answerable from a corpus that still spells it the old way.
        var expansion = new AliasExpansion(["ExampleFormula"], ["ExampleFormula"], [9001], []);
        Assert.Equal("what is OldExample ExampleFormula", KnowledgeTools.BuildQueryText("what is OldExample", expansion));
    }

    [Fact]
    public void A_term_already_in_the_query_is_not_repeated()
    {
        var expansion = new AliasExpansion(["ExampleFormula"], ["ExampleFormula"], [9001], []);
        Assert.Equal("tell me about ExampleFormula", KnowledgeTools.BuildQueryText("tell me about ExampleFormula", expansion));
    }

    [Fact]
    public void Bare_part_numbers_reach_the_filter_even_though_they_are_not_aliases()
    {
        IReadOnlyList<string> values = KnowledgeTools.PartNoFilterValues(AliasExpansion.Empty, ["1207"]);
        Assert.Equal(["1207"], values);
    }

    [Fact]
    public void An_unknown_source_type_is_corrected_rather_than_passed_through()
    {
        // A filter on a type that does not exist returns zero hits, which the
        // model would read as "the corpus has nothing" — the expensive mistype.
        Assert.Null(KnowledgeTools.NormalizeSourceType("pdsrgg", out string? error));
        Assert.NotNull(error);
        Assert.Contains("pdsrg", error, StringComparison.Ordinal);

        Assert.Equal("qa", KnowledgeTools.NormalizeSourceType(" QA ", out string? clean));
        Assert.Null(clean);
        Assert.Null(KnowledgeTools.NormalizeSourceType(null, out _));
    }

    [Fact]
    public async Task The_query_takes_the_blind_path_and_products_take_the_mention_path()
    {
        // tier rule, mapped onto the tool's two inputs: an LLM-only alias
        // resolves when the model names it as a product, and not when it merely
        // appears in the query text.
        KnowledgeTools tools = Tools(out FakeSearch search, out _);
        var function = (AIFunction)tools.AsTools()[0];

        await function.InvokeAsync(new AIFunctionArguments { ["query"] = "tell me about the example one" });
        Assert.DoesNotContain("9001", search.Queries[0].AdditionalFilter ?? "", StringComparison.Ordinal);

        await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "tell me about it",
            ["products"] = new[] { "the example one" },
        });
        Assert.Contains("9001", search.Queries[1].AdditionalFilter ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_product_name_in_the_query_text_widens_the_search_and_never_restricts_it()
    {
        // `Expand` merges both paths into one PartNos list, so without care a
        // family name, a deterministic alias or a legacy name that merely
        // *appeared in the question* would become a hard products/any(...) —
        // and 59% of the corpus (every podcast, every info page, every menu
        // description, much of PDSRG and QA) has no products tag at all, so all
        // of it would fall out of the candidate set.
        //
        // The blind path widens the query text. Only `products` narrows.
        KnowledgeTools tools = Tools(out FakeSearch search, out _);
        var function = (AIFunction)tools.AsTools()[0];

        foreach (string query in new[]
                 {
                     "is ExampleFormula safe with creatine?",   // family name
                     "how much EF per day?",                    // deterministic alias
                     "is OldExample still sold?",               // legacy rename
                 })
        {
            await function.InvokeAsync(new AIFunctionArguments { ["query"] = query });
        }

        Assert.All(search.Queries, q => Assert.Null(q.AdditionalFilter));
        // …while still reaching the query text, which is what expansion is for.
        Assert.Contains("ExampleFormula", search.Queries[1].QueryText, StringComparison.Ordinal);
        Assert.Contains("ExampleFormula", search.Queries[2].QueryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_type_search_keeps_its_own_filter_and_gains_no_product_one()
    {
        // The reproduction from the defect report: no podcast document carries
        // a products tag, so a product filter inferred from the question text
        // could only ever return nothing.
        KnowledgeTools tools = Tools(out FakeSearch search, out _);
        var function = (AIFunction)tools.AsTools()[0];

        await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "what did the podcast say about ExampleFormula?",
            ["source_type"] = "podcast",
        });

        Assert.Equal("(source_type eq 'podcast')", search.Queries[0].AdditionalFilter);
    }

    [Fact]
    public async Task A_restricted_search_says_it_is_restricted_and_names_the_way_out()
    {
        // "Searched with: X" reads as expansion. A model that cannot tell it
        // has a filter cannot take its documented recovery — dropping it.
        KnowledgeTools tools = Tools(out FakeSearch search, out _);
        var function = (AIFunction)tools.AsTools()[0];

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "is it safe with creatine?",
            ["products"] = new[] { "ExampleFormula" },
        });

        Assert.Contains("9001", search.Queries[0].AdditionalFilter ?? "", StringComparison.Ordinal);
        string text = result?.ToString() ?? "";
        Assert.Contains("Restricted to part numbers", text, StringComparison.Ordinal);
        Assert.Contains("Omit `products`", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_restricted_result_is_not_reported_as_an_empty_corpus()
    {
        // Zero hits under a filter is a fact about the filter. Reported as a
        // fact about the corpus, it invites the model to tell a customer dotFIT
        // has nothing on a topic it has plenty on.
        var tools = new KnowledgeTools(
            new FakeSearch(), new FakeDocumentStore(), Fixtures.Aliases(), new AssistantOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)));
        var function = (AIFunction)tools.AsTools()[0];

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "is it safe with creatine?",
            ["products"] = new[] { "ExampleFormula" },
        });

        Assert.Contains("under those filters", result?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_name_is_reported_even_when_another_name_produced_a_note()
    {
        // A rename note must not swallow the warning: the model has to hear
        // that the second name matched nothing.
        KnowledgeTools tools = Tools(out _, out _);
        var function = (AIFunction)tools.AsTools()[0];

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "can I take them together?",
            ["products"] = new[] { "OldExample", "NotAThing" },
        });

        string text = result?.ToString() ?? "";
        Assert.Contains("renamed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"NotAThing\" did not resolve", text, StringComparison.Ordinal);
        // A name that resolves to a note rather than to a family has resolved.
        Assert.DoesNotContain("\"OldExample\" did not resolve", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_query_knobs_come_from_the_search_settings()
    {
        // SemanticDefault and VectorCandidates must reach SearchParameters, or
        // the settings object looks wired and is not.
        KnowledgeTools tools = Tools(out FakeSearch search, out _, settings:
            new SearchSettings { SemanticDefault = true, VectorCandidates = 17 });
        var function = (AIFunction)tools.AsTools()[0];

        await function.InvokeAsync(new AIFunctionArguments { ["query"] = "creatine dosing" });

        Assert.True(search.Queries[0].Semantic);
        Assert.Equal(17, search.Queries[0].VectorCandidates);
    }

    [Fact]
    public async Task A_rename_note_reaches_the_model_with_the_sources()
    {
        KnowledgeTools tools = Tools(out _, out _);
        var function = (AIFunction)tools.AsTools()[0];

        object? result = await function.InvokeAsync(new AIFunctionArguments { ["query"] = "is OldExample still sold?" });

        Assert.Contains("renamed", result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_product_returns_the_whole_family_and_says_it_is_quotable()
    {
        KnowledgeTools tools = Tools(out _, out FakeDocumentStore store);
        var function = (AIFunction)tools.AsTools()[2];

        object? result = await function.InvokeAsync(
            new AIFunctionArguments { ["name_or_part_no"] = "ExampleFormula" });

        string text = result?.ToString() ?? "";
        Assert.Contains("quote this wording directly", text, StringComparison.Ordinal);
        Assert.Contains("source_type eq 'product'", store.Filters[0], StringComparison.Ordinal);
        Assert.Contains("9001", store.Filters[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_product_gets_the_family_list_rather_than_silence()
    {
        KnowledgeTools tools = Tools(out _, out _);
        var function = (AIFunction)tools.AsTools()[2];

        object? result = await function.InvokeAsync(
            new AIFunctionArguments { ["name_or_part_no"] = "NotAThing" });

        string text = result?.ToString() ?? "";
        Assert.Contains("does not resolve", text, StringComparison.Ordinal);
        Assert.Contains("ExampleFormula", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_pulls_the_numbered_neighbours_of_a_chunk()
    {
        var store = new FakeDocumentStore(
            Fixtures.Document(id: "pdsrg-example-001"),
            Fixtures.Document(id: "pdsrg-example-002"),
            Fixtures.Document(id: "pdsrg-example-003"));
        var tools = new KnowledgeTools(
            new FakeSearch(), store, Fixtures.Aliases(), new AssistantOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)));
        var function = (AIFunction)tools.AsTools()[1];

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["id"] = "pdsrg-example-002",
            ["neighbors"] = true,
        });

        string text = result?.ToString() ?? "";
        Assert.Contains("pdsrg-example-001", text, StringComparison.Ordinal);
        Assert.Contains("pdsrg-example-003", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unnumbered_record_says_it_has_no_neighbours_rather_than_guessing()
    {
        var store = new FakeDocumentStore(Fixtures.Document(id: "qa-deadbeefdeadbeef", sourceType: "qa", authority: 3));
        var tools = new KnowledgeTools(
            new FakeSearch(), store, Fixtures.Aliases(), new AssistantOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)));
        var function = (AIFunction)tools.AsTools()[1];

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["id"] = "qa-deadbeefdeadbeef",
            ["neighbors"] = true,
        });

        Assert.Contains("no numbered neighbours", result?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_id_is_an_ordinary_answer_not_an_exception()
    {
        KnowledgeTools tools = Tools(out _, out _);
        var function = (AIFunction)tools.AsTools()[1];

        object? result = await function.InvokeAsync(new AIFunctionArguments { ["id"] = "made-up-999" });

        Assert.Contains("No document has id", result?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_product_gives_one_number_per_page_not_per_section()
    {
        var store = new FakeDocumentStore(
            SourceLedgerTests.Section("product-9001-a", "https://example.com/p/9001"),
            SourceLedgerTests.Section("product-9001-b", "https://example.com/p/9001"),
            SourceLedgerTests.Section("product-9001-c", "https://example.com/p/9001"),
            SourceLedgerTests.Section("product-9002-a", "https://example.com/p/9002"),
            SourceLedgerTests.Section("product-9002-b", "https://example.com/p/9002"));
        var tools = new KnowledgeTools(
            new FakeSearch(), store, Fixtures.Aliases(), new AssistantOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)));

        await ((AIFunction)tools.AsTools()[2]).InvokeAsync(
            new AIFunctionArguments { ["name_or_part_no"] = "ExampleFormula" });

        Assert.Equal(2, tools.Ledger.Sources.Count);
        ToolCallRecord call = Assert.Single(tools.Calls);
        Assert.Equal(2, call.ResultCount);
        Assert.Equal(2, call.NewSourceCount);
    }

    [Fact]
    public async Task A_searched_section_and_its_page_from_get_product_are_the_same_source()
    {
        var search = new FakeSearch(SourceLedgerTests.Section("product-9001-b", "https://example.com/p/9001"));
        var store = new FakeDocumentStore(
            SourceLedgerTests.Section("product-9001-a", "https://example.com/p/9001"),
            SourceLedgerTests.Section("product-9001-b", "https://example.com/p/9001"));
        var tools = new KnowledgeTools(
            search, store, Fixtures.Aliases(), new AssistantOptions(),
            new SourceLedger(6000), new ToolBudget(8, TimeSpan.FromMinutes(5)));

        await ((AIFunction)tools.AsTools()[0]).InvokeAsync(new AIFunctionArguments { ["query"] = "example" });
        object? result = await ((AIFunction)tools.AsTools()[2]).InvokeAsync(
            new AIFunctionArguments { ["name_or_part_no"] = "ExampleFormula" });

        Assert.Single(tools.Ledger.Sources);
        string text = result?.ToString() ?? "";
        Assert.Contains("already given to you earlier this turn", text, StringComparison.Ordinal);
        // The number was given; the rest of the page was not, so it is still shown.
        Assert.Contains("section id: product-9001-a", text, StringComparison.Ordinal);
    }
}
