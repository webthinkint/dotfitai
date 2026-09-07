namespace DotFit.Agents.Retrieval;

/// <summary>
/// Deterministic authority re-rank: multiply the ranking score by a per-tier
/// weight (§3: 1 product copy … 5 menus). Pure and order-stable so it is
/// unit-testable; the weights themselves are a tuning knob for the golden-set
/// eval, not a correctness claim.
/// </summary>
public static class AuthorityBoost
{
    /// <summary>Index 0 is unused; authorities are 1–5. All-ones disables the boost.</summary>
    public static readonly double[] DefaultWeights = [0, 1.10, 1.06, 1.00, 0.95, 0.90];

    /// <summary>
    /// The score the re-rank orders on: the semantic ranker's score when the
    /// ranker ran, the service's BM25/RRF score otherwise. Azure keeps the two
    /// apart — with the ranker on, <c>@search.score</c> is still the fused
    /// retrieval score, so ordering on it would pay for the ranker and then
    /// throw its judgment away. One weight vector serves both modes because a
    /// multiplicative boost is scale-free; only the spread differs (reranker
    /// scores separate further, so the same weight flips fewer near-ties).
    /// </summary>
    public static double RankingScore(RetrievedDocument document) =>
        document.RerankerScore ?? document.Score;

    public static double Apply(double score, int authority, IReadOnlyList<double> weights) =>
        authority >= 1 && authority < weights.Count ? score * weights[authority] : score;

    /// <summary>Assign BoostedScore, order by it (id as the stable tiebreak), truncate.</summary>
    public static List<RetrievedDocument> Rerank(
        IEnumerable<RetrievedDocument> documents, IReadOnlyList<double> weights, int top)
    {
        return documents
            .Select(d => d with { BoostedScore = Apply(RankingScore(d), d.Authority, weights) })
            .OrderByDescending(d => d.BoostedScore)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, top))
            .ToList();
    }
}
