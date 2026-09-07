namespace DotFit.Agents.Retrieval;

/// <summary>
/// Deterministic authority re-rank: multiply the service score by a per-tier
/// weight (§3: 1 product copy … 5 menus). Pure and order-stable so it is
/// unit-testable; the weights themselves are a tuning knob for the golden-set
/// eval, not a correctness claim.
/// </summary>
public static class AuthorityBoost
{
    /// <summary>Index 0 is unused; authorities are 1–5. All-ones disables the boost.</summary>
    public static readonly double[] DefaultWeights = [0, 1.10, 1.06, 1.00, 0.95, 0.90];

    public static double Apply(double score, int authority, IReadOnlyList<double> weights) =>
        authority >= 1 && authority < weights.Count ? score * weights[authority] : score;

    /// <summary>Assign BoostedScore, order by it (id as the stable tiebreak), truncate.</summary>
    public static List<RetrievedDocument> Rerank(
        IEnumerable<RetrievedDocument> documents, IReadOnlyList<double> weights, int top)
    {
        return documents
            .Select(d => d with { BoostedScore = Apply(d.Score, d.Authority, weights) })
            .OrderByDescending(d => d.BoostedScore)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, top))
            .ToList();
    }
}
