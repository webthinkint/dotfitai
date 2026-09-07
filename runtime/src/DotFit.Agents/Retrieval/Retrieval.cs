namespace DotFit.Agents.Retrieval;

/// <summary>
/// One row of the <c>kb-main</c> index (plan §9 schema), as retrieved.
/// <see cref="Score"/> is always the service retrieval score (BM25, or the
/// RRF fusion for a hybrid query) and <see cref="RerankerScore"/> is the
/// semantic ranker's, populated only when the ranker ran — Azure reports the
/// two separately. <see cref="BoostedScore"/> is after the deterministic
/// authority re-rank, which orders on
/// <see cref="AuthorityBoost.RankingScore"/>.
/// </summary>
public sealed record RetrievedDocument
{
    public required string Id { get; init; }
    public required string SourceType { get; init; }
    /// <summary>§3 authority tier: 1 product copy, 2 PDSRG, 3 QA, 4 podcast, 5 menus.</summary>
    public required int Authority { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string? CitationUrl { get; init; }
    public string? Locator { get; init; }
    public IReadOnlyList<string> Products { get; init; } = [];
    public IReadOnlyList<string> Topics { get; init; } = [];
    public DateTimeOffset? Date { get; init; }
    public required bool IsCurrent { get; init; }
    public string? ProductStatus { get; init; }
    public double Score { get; init; }
    public double? RerankerScore { get; init; }
    public double BoostedScore { get; init; }
}

/// <summary>What to search for and how (built by the orchestrator / CLI).</summary>
public sealed record SearchParameters
{
    /// <summary>BM25 text — the canonical question plus alias-expanded terms.</summary>
    public required string QueryText { get; init; }
    /// <summary>Documents fed to the answer agent (after the authority re-rank).</summary>
    public int Top { get; init; } = 8;
    /// <summary>Vector KNN candidates (k) before fusion.</summary>
    public int VectorCandidates { get; init; } = 50;
    /// <summary>Semantic ranker (open item 5 — decided empirically on the golden set).</summary>
    public bool Semantic { get; init; }
    /// <summary>Extra OData filter, ANDed with the is_current filter.</summary>
    public string? AdditionalFilter { get; init; }
}

public sealed record SearchSettings
{
    public int DefaultTop { get; init; } = 8;
    public int VectorCandidates { get; init; } = 50;
    public bool SemanticDefault { get; init; } = false;
    public string SemanticConfiguration { get; init; } = "sem-default";
    public string CurrentFilter { get; init; } = "is_current eq true";
    /// <summary>
    /// Deterministic authority re-rank (plan §11 "boost authority"). Mild by
    /// design — tuned against the golden set in week 4, alongside the ranker
    /// decision (open item 5). All-ones disables it.
    /// </summary>
    public IReadOnlyList<double> AuthorityWeights { get; init; } = AuthorityBoost.DefaultWeights;
}

public interface IKnowledgeSearch
{
    Task<IReadOnlyList<RetrievedDocument>> SearchAsync(SearchParameters parameters, CancellationToken ct = default);
}
