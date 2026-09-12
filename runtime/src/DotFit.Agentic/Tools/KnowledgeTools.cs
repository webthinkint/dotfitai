using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotFit.Agentic.Config;
using DotFit.Agentic.Retrieval;
using DotFit.Agentic.Turn;
using DotFit.Agents.Aliases;
using DotFit.Agents.Retrieval;
using Microsoft.Extensions.AI;

namespace DotFit.Agentic.Tools;

/// <summary>
/// The corpus as three tools (design §7): <c>search</c>, <c>fetch</c>,
/// <c>get_product</c>. This is the entire surface the model can reach — there
/// is no general-knowledge path to a dotFIT product fact, which is the second
/// of the three mechanisms that replaced v1's gate (§8.2).
///
/// Everything here is deterministic and calls no model. Alias expansion,
/// filters, numbering and budget are code; what to look up and what to say
/// with it is the model's job and nothing else's.
///
/// **One instance per turn.** The ledger, the budget and the call records are
/// turn-scoped, so the tools are built fresh for each question and handed to
/// the agent as per-run options. The Agent Framework may invoke them
/// concurrently when the model issues parallel calls — hence the lock in
/// <see cref="SourceLedger"/> and the interlocked counter in
/// <see cref="ToolBudget"/>.
/// </summary>
public sealed partial class KnowledgeTools
{
    private readonly IKnowledgeSearch _search;
    private readonly IDocumentStore _store;
    private readonly AliasTable _aliases;
    private readonly AgenticOptions _options;
    private readonly List<ToolCallRecord> _calls = [];
    private readonly SortedSet<string> _families = new(StringComparer.Ordinal);
    private readonly Lock _callGate = new();

    public KnowledgeTools(
        IKnowledgeSearch search,
        IDocumentStore store,
        AliasTable aliases,
        AgenticOptions options,
        SourceLedger ledger,
        ToolBudget budget)
    {
        _search = search;
        _store = store;
        _aliases = aliases;
        _options = options;
        Ledger = ledger;
        Budget = budget;
    }

    public SourceLedger Ledger { get; }
    public ToolBudget Budget { get; }

    /// <summary>Every tool call this turn, in completion order, for the log and the trace.</summary>
    public IReadOnlyList<ToolCallRecord> Calls
    {
        get { lock (_callGate) return [.. _calls]; }
    }

    /// <summary>
    /// Product families the alias table resolved this turn. Loggable where the
    /// question is not (§10): these are the §5 artifact's names, our
    /// vocabulary, not the customer's words.
    /// </summary>
    public IReadOnlyList<string> Families
    {
        get { lock (_callGate) return [.. _families]; }
    }

    /// <summary>
    /// The tools as the Agent Framework sees them. Names and descriptions are
    /// the model's only documentation for the corpus, so they carry the
    /// §3 authority story rather than describing an index.
    /// </summary>
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(
            SearchAsync,
            name: "search",
            description:
                "Search dotFIT's knowledge base: approved product copy and website pages (authority 1), the " +
                "Practitioner Dietary Supplement Reference Guide (2), answers dotFIT experts wrote to real " +
                "customers (3), podcast transcripts (4) and meal-plan descriptions (5). Hybrid keyword + " +
                "semantic search over current documents only. Product names are alias-expanded for you, " +
                "including discontinued and renamed ones. Search more than once — narrow, rephrase, or search " +
                "for a different aspect — rather than settling for a thin first result."),
        AIFunctionFactory.Create(
            FetchAsync,
            name: "fetch",
            description:
                "Retrieve one document by its id, exactly as returned by search. Use it when a source you were " +
                "given is truncated, or when a table, dosing protocol or list appears to be cut off mid-way. " +
                "Set neighbors=true to also get the chunks either side of it in the same document."),
        AIFunctionFactory.Create(
            GetProductAsync,
            name: "get_product",
            description:
                "Get the complete legal-approved copy for one dotFIT product family, by name or part number, " +
                "including discontinued and former names. This is approved wording you may quote directly. " +
                "Call it before making any claim about what a product contains, does, or is for — it is the " +
                "shortest route to wording you are allowed to use."),
    ];

    // ---------------------------------------------------------------- search

    [Description("Search the dotFIT knowledge base and get back numbered sources you can cite.")]
    private async Task<string> SearchAsync(
        [Description("What to search for, in plain language. Full questions work better than keywords.")]
        string query,
        [Description("Restrict to one corpus: product, infopage, pdsrg, qa, podcast, menu_desc. Omit to search everything.")]
        string? source_type = null,
        [Description("Restrict to these product names or part numbers. Names are alias-resolved; former names work.")]
        string[]? products = null,
        [Description("How many sources to return. Default 6, maximum 20.")]
        int? top = null,
        CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        if (!Budget.TryConsume(out string refusal))
            return Record(Stages.Search, query, refusal, clock);

        Ledger.Stage(Stages.Search, query);

        // The alias tier contract, mapped onto the tool's two inputs (§5, §7.1):
        // `products` is a *judged* product mention — the model decided it names a
        // product — so it takes the mention path and may resolve LLM-only
        // aliases. `query` is raw text and takes the blind path, deterministic
        // tier only. Passing the query as a mention would let a blind scan
        // resolve tokens the pipeline says it must not.
        AliasExpansion expansion = _aliases.Expand(query, products);
        NoteFamilies(expansion);

        string? sourceTypeFilter = NormalizeSourceType(source_type, out string? sourceTypeError);
        if (sourceTypeError is not null)
            return Record(Stages.Search, query, sourceTypeError, clock);

        string productFilter = Filters.AnyProduct(PartNoFilterValues(expansion, products));
        string filter = Filters.And(
            sourceTypeFilter is null ? null : Filters.SourceType(sourceTypeFilter),
            productFilter);

        int requested = Math.Clamp(top ?? _options.DefaultTop, 1, _options.MaxTop);
        IReadOnlyList<RetrievedDocument> hits;
        try
        {
            hits = await _search.SearchAsync(new SearchParameters
            {
                QueryText = BuildQueryText(query, expansion),
                Top = requested,
                AdditionalFilter = string.IsNullOrEmpty(filter) ? null : filter,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Record(Stages.Search, query, $"The search failed: {e.Message}. Try a different query.", clock);
        }

        var body = new StringBuilder();
        AppendExpansionNotes(body, expansion, products);

        if (hits.Count == 0)
        {
            body.AppendLine(
                "No current documents matched. Try broader wording, drop the filters, or search for the " +
                "underlying nutrient or goal rather than the product name.");
            return Record(Stages.Search, query, body.ToString(), clock, 0, 0);
        }

        int newCount = AppendSources(body, hits);
        return Record(Stages.Search, query, body.ToString(), clock, hits.Count, newCount);
    }

    // ----------------------------------------------------------------- fetch

    [Description("Retrieve one knowledge-base document by id.")]
    private async Task<string> FetchAsync(
        [Description("The document id, copied exactly from a source you were given.")]
        string id,
        [Description("Also return the chunks immediately before and after this one.")]
        bool neighbors = false,
        CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        if (!Budget.TryConsume(out string refusal))
            return Record(Stages.Fetch, id, refusal, clock);

        Ledger.Stage(Stages.Fetch, id);

        RetrievedDocument? document;
        try
        {
            document = await _store.GetAsync(id.Trim(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Record(Stages.Fetch, id, $"The fetch failed: {e.Message}.", clock);
        }

        if (document is null)
            return Record(Stages.Fetch, id,
                $"No document has id \"{id}\". Ids come from search results — copy one exactly, or search again.",
                clock, 0, 0);

        var found = new List<RetrievedDocument> { document };
        if (neighbors)
            found.AddRange(await NeighborsAsync(document.Id, ct).ConfigureAwait(false));

        var body = new StringBuilder();
        if (neighbors && found.Count == 1)
            body.AppendLine(
                "This document has no numbered neighbours — it is a whole record, not one chunk of a longer " +
                "one. What follows is all of it.");
        int newCount = AppendSources(body, found);
        return Record(Stages.Fetch, id, body.ToString(), clock, found.Count, newCount);
    }

    /// <summary>
    /// Chunked corpora number their pieces in the id — <c>pdsrg-{stem}-017</c>,
    /// <c>podcast-{episode}-011</c> — so the neighbours of a chunk are two key
    /// lookups, no filter needed (the key field is not filterable, so a range
    /// query is not available even if it were nicer). QA records and website
    /// sections carry no ordinal, and for those "no neighbours" is the honest
    /// answer rather than a guess at adjacency.
    /// </summary>
    private async Task<IReadOnlyList<RetrievedDocument>> NeighborsAsync(string id, CancellationToken ct)
    {
        Match match = ChunkIdRegex().Match(id);
        if (!match.Success)
            return [];

        string stem = match.Groups["stem"].Value;
        string ordinal = match.Groups["n"].Value;
        int n = int.Parse(ordinal);

        var found = new List<RetrievedDocument>();
        foreach (int neighbor in new[] { n - 1, n + 1 })
        {
            if (neighbor < 0)
                continue;
            string neighborId = $"{stem}-{neighbor.ToString(new string('0', ordinal.Length))}";
            RetrievedDocument? document = await _store.GetAsync(neighborId, ct).ConfigureAwait(false);
            if (document is not null)
                found.Add(document);
        }
        return found;
    }

    // ----------------------------------------------------------- get_product

    [Description("Get the complete approved copy for one dotFIT product family.")]
    private async Task<string> GetProductAsync(
        [Description("Product name or part number. Former and discontinued names are resolved for you.")]
        string name_or_part_no,
        CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        if (!Budget.TryConsume(out string refusal))
            return Record(Stages.Product, name_or_part_no, refusal, clock);

        Ledger.Stage(Stages.Product, name_or_part_no);

        // The model named this as a product, so it is a judged mention: the
        // mention path, which may resolve the LLM-only tier (§5).
        AliasExpansion expansion = _aliases.Expand("", [name_or_part_no]);
        NoteFamilies(expansion);
        IReadOnlyList<string> partNos = PartNoFilterValues(expansion, [name_or_part_no]);

        var body = new StringBuilder();
        AppendExpansionNotes(body, expansion, [name_or_part_no]);

        if (partNos.Count == 0)
        {
            body.AppendLine(
                $"\"{name_or_part_no}\" does not resolve to a current dotFIT product family." +
                (expansion.Notes.Count > 0
                    ? " The note above is what is known about it."
                    : " Check the name, or use search to find out what it is."));
            body.AppendLine();
            body.AppendLine("Current families: " + string.Join(", ",
                _aliases.Families.Select(f => f.Family).OrderBy(f => f, StringComparer.Ordinal)));
            return Record(Stages.Product, name_or_part_no, body.ToString(), clock, 0, 0);
        }

        string filter = Filters.And(Filters.SourceType("product"), Filters.AnyProduct(partNos));
        IReadOnlyList<RetrievedDocument> sections;
        try
        {
            // A family's sections are a handful of documents; 50 is headroom,
            // not a page size — this tool returns the whole record or it has
            // not done its job.
            sections = await _store.FilterAsync(filter, top: 50, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Record(Stages.Product, name_or_part_no, $"The lookup failed: {e.Message}.", clock);
        }

        if (sections.Count == 0)
        {
            body.AppendLine(
                $"\"{name_or_part_no}\" resolves to part numbers {string.Join(", ", partNos)}, but no approved " +
                "product copy is indexed for them. Use search to see what other sources say.");
            return Record(Stages.Product, name_or_part_no, body.ToString(), clock, 0, 0);
        }

        body.Append("Approved copy for ")
            .Append(expansion.Families.Count > 0 ? string.Join(", ", expansion.Families) : name_or_part_no)
            .Append(" (part numbers ").Append(string.Join(", ", partNos)).AppendLine("), complete.");
        body.AppendLine("You may quote this wording directly.");
        body.AppendLine();

        int newCount = AppendSources(body, sections);
        return Record(Stages.Product, name_or_part_no, body.ToString(), clock, sections.Count, newCount);
    }

    // ----------------------------------------------------------------- parts

    /// <summary>
    /// BM25 sees the question plus the alias-expanded family names; the vector
    /// side embeds the same string. Expansion terms are appended rather than
    /// substituted — the customer's own words are usually the best query, and
    /// replacing "LeanMR" with "LeanMeal" would lose the very token that makes
    /// a rename answerable from the corpus that still spells it the old way.
    /// </summary>
    internal static string BuildQueryText(string query, AliasExpansion expansion)
    {
        if (expansion.SearchTerms.Count == 0)
            return query;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = expansion.SearchTerms
            .Where(term => !query.Contains(term, StringComparison.OrdinalIgnoreCase) && seen.Add(term))
            .ToList();
        return extra.Count == 0 ? query : $"{query} {string.Join(" ", extra)}";
    }

    /// <summary>
    /// Part numbers for the <c>products</c> filter: what the alias table
    /// resolved, plus any bare digits the model passed. A bare part number is
    /// not an alias — the table keys on names — so without this a model that
    /// helpfully passes <c>"1207"</c> would filter to nothing.
    /// </summary>
    internal static IReadOnlyList<string> PartNoFilterValues(
        AliasExpansion expansion, IEnumerable<string>? products)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        foreach (int partNo in expansion.PartNos)
            values.Add(partNo.ToString());
        foreach (string raw in products ?? [])
        {
            string token = raw.Trim();
            if (token.Length > 0 && token.All(char.IsAsciiDigit))
                values.Add(token);
        }
        return [.. values];
    }

    /// <summary>
    /// The §9 source types, as a closed set. An unknown value is corrected in
    /// prose rather than passed through: an OData filter on a type that does
    /// not exist returns zero hits, which the model would read as "the corpus
    /// has nothing on this" — the most expensive possible way to mistype.
    /// </summary>
    internal static string? NormalizeSourceType(string? value, out string? error)
    {
        error = null;
        string token = (value ?? "").Trim().ToLowerInvariant();
        if (token.Length == 0)
            return null;
        if (KnownSourceTypes.Contains(token))
            return token;
        error =
            $"\"{value}\" is not a corpus. Use one of: {string.Join(", ", KnownSourceTypes)} — or omit " +
            "source_type to search all of them.";
        return null;
    }

    internal static readonly string[] KnownSourceTypes =
        ["product", "infopage", "pdsrg", "qa", "podcast", "menu_desc"];

    /// <summary>
    /// The currency guidance the alias table produced for this call (§5).
    /// The system prompt already carries the whole rename/replacement list
    /// (§6), but a note beside the sources says which of them is live *now* —
    /// and says out loud that the search ran on terms the model did not type,
    /// which it otherwise has no way to know.
    /// </summary>
    private static void AppendExpansionNotes(
        StringBuilder body, AliasExpansion expansion, IEnumerable<string>? products)
    {
        foreach (string note in expansion.Notes)
            body.Append("Note: ").AppendLine(note);

        var unresolved = (products ?? [])
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.All(char.IsAsciiDigit))
            .Where(p => !expansion.Families.Any(f => AliasTable.Norm(f) == AliasTable.Norm(p)))
            .ToList();
        if (unresolved.Count > 0 && expansion.Notes.Count == 0)
            body.Append("Note: ").Append(string.Join(", ", unresolved.Select(u => $"\"{u}\""))).AppendLine(
                " did not resolve to a known product family, so no product filter was applied. " +
                "It may be a third-party product, an ingredient, or a misspelling.");

        if (expansion.Families.Count > 0)
            body.Append("Searched with: ").AppendLine(string.Join(", ", expansion.Families));
        if (body.Length > 0)
            body.AppendLine();
    }

    private void NoteFamilies(AliasExpansion expansion)
    {
        if (expansion.Families.Count == 0)
            return;
        lock (_callGate)
            foreach (string family in expansion.Families)
                _families.Add(family);
    }

    /// <summary>Number, render and append every hit. Returns how many were new this turn.</summary>
    private int AppendSources(StringBuilder body, IReadOnlyList<RetrievedDocument> documents)
    {
        int newCount = 0;
        foreach (RetrievedDocument document in documents)
        {
            (SourceRef source, bool isNew) = Ledger.Add(document);
            if (isNew)
                newCount++;
            body.AppendLine(Ledger.Render(source, document, isNew));
            body.AppendLine();
        }
        return newCount;
    }

    private string Record(
        string tool, string argument, string result, Stopwatch clock,
        int? resultCount = null, int newCount = 0)
    {
        lock (_callGate)
        {
            _calls.Add(new ToolCallRecord
            {
                Tool = tool,
                Argument = argument,
                ResultCount = resultCount ?? 0,
                NewSourceCount = newCount,
                ElapsedMs = clock.ElapsedMilliseconds,
                Refusal = resultCount is null ? result : null,
            });
        }
        return result.TrimEnd();
    }

    /// <summary>
    /// <c>{stem}-{ordinal}</c>, where the ordinal is the zero-padded chunk
    /// index the pipeline writes (<c>pdsrg-workoutextreme-037</c>). Anchored so
    /// a QA id — <c>qa-{hash}</c>, hex and unnumbered — cannot match.
    /// </summary>
    [GeneratedRegex(@"^(?<stem>.+)-(?<n>\d{2,4})$")]
    private static partial Regex ChunkIdRegex();
}
