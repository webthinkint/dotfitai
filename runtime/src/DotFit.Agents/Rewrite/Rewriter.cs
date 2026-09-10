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
    /// <summary>
    /// <paramref name="history"/> is the earlier turns of this conversation,
    /// oldest first and already trimmed by
    /// <see cref="ConversationHistory.Normalize"/>. It deliberately has no
    /// default: this is the stage that owns follow-up resolution (open item
    /// 18), and a caller that quietly forgot to pass it gets a confidently
    /// wrong-topic answer rather than an obvious failure. Pass <c>[]</c> to
    /// rewrite a question standalone.
    /// </summary>
    Task<RewriteResult> RewriteAsync(
        string question, IReadOnlyList<ConversationTurn> history, CancellationToken ct = default);
}

/// <summary>
/// Small-model query rewrite: canonical question + product mentions as family
/// names. Mentions then go through the deterministic alias expansion (§5) —
/// the LLM never invents part_nos, it only names what it saw.
///
/// This is also where a multi-turn conversation is collapsed back into a
/// single standalone question (open item 18), and the placement is the whole
/// design: the history resolves "the chocolate one" into a product mention,
/// the §5 table resolves that mention into part_nos deterministically, and
/// everything downstream — search, the answer agent, the post-check — keeps
/// seeing one self-contained question and no conversational state at all.
/// </summary>
public sealed class AgentQueryRewriter(AIAgent agent, IReadOnlyList<string> knownFamilies) : IQueryRewriter
{
    public async Task<RewriteResult> RewriteAsync(
        string question, IReadOnlyList<ConversationTurn> history, CancellationToken ct = default)
    {
        string user = Answering.Prompts.BuildRewriteUserMessage(question, knownFamilies, history);
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
