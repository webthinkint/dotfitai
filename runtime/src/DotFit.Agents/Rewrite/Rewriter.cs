using System.Text.Json.Serialization;
using DotFit.Agents.Structured;
using Microsoft.Agents.AI;

namespace DotFit.Agents.Rewrite;

/// <summary>Query-rewrite result (plan §11 stage 2, small model).</summary>
public sealed class RewriteResult
{
    [JsonPropertyName("canonical_question")] public string CanonicalQuestion { get; set; } = "";
    [JsonPropertyName("product_mentions")] public List<string> ProductMentions { get; set; } = [];
    [JsonPropertyName("topics")] public List<string> Topics { get; set; } = [];
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    /// <summary>True when the rewrite fell back (API/parse failure) — the raw question is used as-is.</summary>
    public bool Degraded { get; set; }
    public string? DegradedReason { get; set; }
}

public interface IQueryRewriter
{
    Task<RewriteResult> RewriteAsync(string question, CancellationToken ct = default);
}

/// <summary>
/// Small-model query rewrite: canonical question + product mentions as family
/// names. Mentions then go through the deterministic alias expansion (§5) —
/// the LLM never invents part_nos, it only names what it saw.
/// </summary>
public sealed class AgentQueryRewriter(AIAgent agent, IReadOnlyList<string> knownFamilies) : IQueryRewriter
{
    public async Task<RewriteResult> RewriteAsync(string question, CancellationToken ct = default)
    {
        string user =
            $"Customer question: {question}\n\n" +
            $"Known dotFIT product families: {string.Join("; ", knownFamilies)}";
        try
        {
            return await StructuredCall.RunAsync<RewriteResult>(
                agent, user, "dotfit_rewrite", Schemas.Rewrite, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new RewriteResult
            {
                CanonicalQuestion = question,
                ProductMentions = [],
                Topics = [],
                Confidence = 0,
                Degraded = true,
                DegradedReason = e.Message,
            };
        }
    }
}
