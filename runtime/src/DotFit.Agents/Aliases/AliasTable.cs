using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotFit.Agents.Aliases;

/// <summary>Alias table is missing, malformed, or incomplete.</summary>
public sealed class AliasTableException(string message) : Exception(message);

public sealed record AliasFamily(
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("canonical_part_no")] int CanonicalPartNo,
    [property: JsonPropertyName("part_nos")] int[] PartNos);

public sealed record AliasEntry(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("part_nos")] int[] PartNos);

public sealed record LegacyRename(
    [property: JsonPropertyName("deprecated")] string Deprecated,
    [property: JsonPropertyName("current_family")] string CurrentFamily,
    [property: JsonPropertyName("part_nos")] int[] PartNos);

public sealed record Replacement(
    [property: JsonPropertyName("deprecated")] string Deprecated,
    [property: JsonPropertyName("successor_family")] string SuccessorFamily,
    [property: JsonPropertyName("successor_part_nos")] int[] SuccessorPartNos,
    [property: JsonPropertyName("note")] string? Note);

public sealed record Discontinued(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("note")] string? Note);

/// <summary>
/// The result of query-side alias expansion (plan §5): terms that widen the
/// BM25 query, resolved families/part_nos for trace and post-check, and notes
/// the answer agent needs (renames are identity — "LeanMR, now LeanMeal";
/// replacements are a different formula and must never be conflated;
/// context-only tokens resolve per-document, never to a blanket tag).
/// </summary>
public sealed record AliasExpansion(
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> Families,
    IReadOnlyList<int> PartNos,
    IReadOnlyList<string> Notes)
{
    public static readonly AliasExpansion Empty = new([], [], [], []);
}

/// <summary>
/// The committed <c>processed/aliases/alias_table.json</c> artifact (§5
/// output, versioned by the pipeline). Query-side expansion only — corpus
/// text is never rewritten through this table.
///
/// Two consumers with different context, mirroring the pipeline's tier rules:
/// the *mention path* (LLM-judged mentions from the rewriter) may resolve
/// deterministic AND llm-only aliases; the *blind path* (raw question text)
/// may resolve deterministic aliases only. Context-only tokens (PP, MVM)
/// resolve in neither — they contribute guidance notes for the answer agent.
/// </summary>
public sealed class AliasTable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Version { get; }
    public IReadOnlyList<AliasFamily> Families { get; }
    public IReadOnlyList<AliasEntry> DeterministicAliases { get; }
    public IReadOnlyList<AliasEntry> LlmOnlyAliases { get; }
    public IReadOnlyDictionary<string, string> ContextOnlyTokens { get; }
    public IReadOnlyList<LegacyRename> LegacyRenames { get; }
    public IReadOnlyList<Replacement> Replacements { get; }
    public IReadOnlyList<Discontinued> DiscontinuedProducts { get; }

    private readonly Dictionary<string, AliasFamily> _familiesByNorm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AliasEntry> _deterministicByNorm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AliasEntry> _llmOnlyByNorm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyRename> _renamesByNorm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Replacement> _replacementsByNorm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Discontinued> _discontinuedByNorm = new(StringComparer.Ordinal);

    private AliasTable(Dto dto)
    {
        Version = dto.Version ?? "";
        Families = dto.Families ?? [];
        DeterministicAliases = dto.DeterministicAliases ?? [];
        LlmOnlyAliases = dto.LlmOnlyAliases ?? [];
        ContextOnlyTokens = dto.ContextOnlyTokens ?? new Dictionary<string, string>();
        LegacyRenames = dto.LegacyRenames ?? [];
        Replacements = dto.Replacements ?? [];
        DiscontinuedProducts = dto.Discontinued ?? [];

        foreach (var f in Families) _familiesByNorm[Norm(f.Family)] = f;
        foreach (var a in DeterministicAliases) _deterministicByNorm[Norm(a.Token)] = a;
        foreach (var a in LlmOnlyAliases) _llmOnlyByNorm[Norm(a.Token)] = a;
        foreach (var r in LegacyRenames) _renamesByNorm[Norm(r.Deprecated)] = r;
        foreach (var r in Replacements) _replacementsByNorm[Norm(r.Deprecated)] = r;
        foreach (var d in DiscontinuedProducts) _discontinuedByNorm[Norm(d.Name)] = d;
    }

    /// <summary>The artifact's own normalization: NFKC, casefold, strip non-alphanumeric.</summary>
    public static string Norm(string value)
    {
        if (value.Length == 0)
            return "";
        string k = value.Normalize(NormalizationForm.FormKC);
        var sb = new System.Text.StringBuilder(k.Length);
        foreach (char ch in k)
        {
            char c = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static AliasTable Load(string path)
    {
        if (!File.Exists(path))
            throw new AliasTableException(
                $"alias table not found at {path} — regenerate it with `uv run qa-pipeline aliases` " +
                "(from pipeline/) or pass --aliases <path>");
        return FromJson(File.ReadAllText(path));
    }

    public static AliasTable FromJson(string json)
    {
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new AliasTableException($"alias table is not valid JSON: {e.Message}");
        }
        if (dto is null)
            throw new AliasTableException("alias table is empty");
        foreach (var (section, present) in new[]
        {
            ("families", dto.Families is not null),
            ("deterministic_aliases", dto.DeterministicAliases is not null),
            ("llm_only_aliases", dto.LlmOnlyAliases is not null),
            ("context_only_tokens", dto.ContextOnlyTokens is not null),
            ("legacy_renames", dto.LegacyRenames is not null),
            ("replacements", dto.Replacements is not null),
            ("discontinued", dto.Discontinued is not null),
        })
        {
            if (!present)
                throw new AliasTableException(
                    $"alias table section '{section}' is missing — regenerate with `uv run qa-pipeline aliases`");
        }
        return new AliasTable(dto);
    }

    /// <summary>
    /// Expand the question (blind scan, deterministic tier) plus optional
    /// LLM-judged mentions (deterministic + llm-only tiers). Unresolved
    /// mentions are not an error — they are a curation signal, same as Stage 2.
    /// </summary>
    public AliasExpansion Expand(string question, IReadOnlyList<string>? llmMentions = null)
    {
        var families = new List<string>();
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        var partNos = new SortedSet<int>();
        var searchTerms = new List<string>();
        var notes = new List<string>();
        var seenNotes = new HashSet<string>(StringComparer.Ordinal);

        void AddFamily(string family, int[] partNosOf)
        {
            if (seenFamilies.Add(family))
            {
                families.Add(family);
                searchTerms.Add(family);
            }
            foreach (int pn in partNosOf)
                partNos.Add(pn);
        }

        void AddNote(string note)
        {
            if (seenNotes.Add(note))
                notes.Add(note);
        }

        // --- mention path: the rewriter already judged these to be products ----
        foreach (string mention in llmMentions ?? [])
        {
            string n = Norm(mention);
            if (n.Length == 0)
                continue;
            if (_familiesByNorm.TryGetValue(n, out var fam))
            {
                AddFamily(fam.Family, fam.PartNos);
            }
            else if (_deterministicByNorm.TryGetValue(n, out var det))
            {
                AddFamily(det.Family, det.PartNos);
            }
            else if (_llmOnlyByNorm.TryGetValue(n, out var llm))
            {
                AddFamily(llm.Family, llm.PartNos); // LLM-only tier: mention path only (§5)
            }
            else if (_renamesByNorm.TryGetValue(n, out var rename))
            {
                AddFamily(rename.CurrentFamily, rename.PartNos);
                AddNote($"{rename.Deprecated} was renamed {rename.CurrentFamily} — the same product, the same " +
                        "formula. Use the current name in the answer and mention the rename.");
            }
            else if (_replacementsByNorm.TryGetValue(n, out var rep))
            {
                AddNote($"{rep.Deprecated} is discontinued and was replaced by {rep.SuccessorFamily} — a " +
                        "different formula. Never present them as the same product; answer about the current " +
                        "line only if the sources support it.");
            }
            else if (_discontinuedByNorm.TryGetValue(n, out var disc))
            {
                AddNote($"{disc.Name} is discontinued. {disc.Note}".TrimEnd());
            }
            // else: unresolved mention — trace-only, a curation signal
        }

        // --- blind path: raw question text, deterministic tier only ------------
        HashSet<string> joins = WordJoins(question);
        foreach (var det in DeterministicAliases)
            if (joins.Contains(Norm(det.Token)))
                AddFamily(det.Family, det.PartNos);
        foreach (var fam in Families)
            if (joins.Contains(Norm(fam.Family)))
                AddFamily(fam.Family, fam.PartNos);
        foreach (var rename in LegacyRenames)
            if (joins.Contains(Norm(rename.Deprecated)))
            {
                AddFamily(rename.CurrentFamily, rename.PartNos);
                AddNote($"{rename.Deprecated} was renamed {rename.CurrentFamily} — the same product, the same " +
                        "formula. Use the current name in the answer and mention the rename.");
            }
        foreach (var rep in Replacements)
            if (joins.Contains(Norm(rep.Deprecated)))
                AddNote($"{rep.Deprecated} is discontinued and was replaced by {rep.SuccessorFamily} — a " +
                        "different formula. Never present them as the same product; answer about the current " +
                        "line only if the sources support it.");
        foreach (var disc in DiscontinuedProducts)
            if (joins.Contains(Norm(disc.Name)))
                AddNote($"{disc.Name} is discontinued. {disc.Note}".TrimEnd());
        foreach (var (token, guidance) in ContextOnlyTokens)
            if (joins.Contains(Norm(token)))
                AddNote($"\"{token}\" is ambiguous here: {guidance}");

        return new AliasExpansion([.. searchTerms], [.. families], [.. partNos], [.. notes]);
    }

    /// <summary>
    /// Word-boundary-safe token matching: the normalized forms of every run of
    /// 1–6 consecutive words. A bare substring match would mis-fire on short
    /// tokens ("pp" inside "happy"), and word runs are what the corpus writes
    /// ("Super Omega 3", "1-Active", "Pre &amp; Post Workout Formula").
    /// </summary>
    private static HashSet<string> WordJoins(string text)
    {
        var words = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            words.Add(sb.ToString());

        const int maxJoin = 6;
        var joins = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < words.Count; i++)
        {
            var join = new System.Text.StringBuilder();
            for (int len = 1; len <= maxJoin && i + len <= words.Count; len++)
            {
                join.Append(words[i + len - 1]);
                joins.Add(Norm(join.ToString()));
            }
        }
        return joins;
    }

    private sealed class Dto
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("families")] public AliasFamily[]? Families { get; set; }
        [JsonPropertyName("deterministic_aliases")] public AliasEntry[]? DeterministicAliases { get; set; }
        [JsonPropertyName("llm_only_aliases")] public AliasEntry[]? LlmOnlyAliases { get; set; }
        [JsonPropertyName("context_only_tokens")] public Dictionary<string, string>? ContextOnlyTokens { get; set; }
        [JsonPropertyName("legacy_renames")] public LegacyRename[]? LegacyRenames { get; set; }
        [JsonPropertyName("replacements")] public Replacement[]? Replacements { get; set; }
        [JsonPropertyName("discontinued")] public Discontinued[]? Discontinued { get; set; }
    }
}
