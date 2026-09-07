using System.Text.Json.Serialization;
using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Structured;
using Microsoft.Agents.AI;

namespace DotFit.Agents.PostCheck;

/// <summary>Claims-language verdict (small model, plan §11 post-check).</summary>
public sealed class ClaimsVerdict
{
    [JsonPropertyName("compliant")] public bool Compliant { get; set; }
    [JsonPropertyName("violations")] public List<string> Violations { get; set; } = [];
    [JsonPropertyName("evidence")] public List<string> Evidence { get; set; } = [];
    /// <summary>True when the check could not run — a warning, never a failure.</summary>
    public bool Degraded { get; set; }
}

/// <summary>Deterministic post-check outcome (plan §11 stage 5).</summary>
public sealed record PostCheckResult(
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    ClaimsVerdict? Claims)
{
    public static PostCheckResult Pass(IReadOnlyList<string> warnings, ClaimsVerdict? claims) =>
        new(true, [], warnings, claims);
};

/// <summary>
/// Deterministic verifiable checks over the assembled answer:
/// citation presence, product-claim citations to authority 1–2, escalation
/// respected, unknown citation markers. The claims-language check is a
/// separate, optional small-model judgment (§12 formalizes the metrics).
/// </summary>
public static class PostChecker
{
    private static readonly string[] HandoffPhrases =
        ["support team", "healthcare professional", "health care professional"];

    public static PostCheckResult Check(
        GuardrailVerdict guardrail,
        RewriteResult rewrite,
        Aliases.AliasExpansion expansion,
        IReadOnlyList<RetrievedDocument> sources,
        string answer,
        ClaimsVerdict? claims = null)
    {
        var failures = new List<string>();
        var warnings = new List<string>();

        IReadOnlyList<int> markers = CitationFormatter.ExtractMarkers(answer);
        IReadOnlyList<int> outOfRange = CitationFormatter.OutOfRange(answer, sources.Count);
        if (outOfRange.Count > 0)
            warnings.Add($"unknown_citations: {string.Join(", ", outOfRange)} (not in the source list)");

        if (guardrail.Escalate)
        {
            // The refusal must hand off and must not hand out product guidance.
            // KnowledgeAssistant templates that refusal, so neither branch can
            // fire from the pipeline; they hold the line for any other caller
            // (the SSE service, the eval harness) that lets a model write it.
            bool handsOff = HandoffPhrases.Any(p => answer.Contains(p, StringComparison.OrdinalIgnoreCase));
            bool cites = markers.Count > 0;
            if (!handsOff)
                failures.Add("escalation_respected: the refusal does not hand off to the support team or a healthcare professional");
            if (cites)
                failures.Add("escalation_respected: an escalated answer must not cite product guidance");
        }
        else
        {
            if (sources.Count > 0 && markers.Count == 0)
                failures.Add("citation_presence: the answer cites none of the retrieved sources");

            bool productClaim = expansion.Families.Count > 0 || guardrail.ClaimTrap
                || rewrite.ProductMentions.Count > 0;
            bool approvedCopyRetrieved = sources.Any(s => s.Authority <= 2);
            if (productClaim && approvedCopyRetrieved)
            {
                bool citesApprovedCopy = markers
                    .Where(n => n >= 1 && n <= sources.Count)
                    .Any(n => sources[n - 1].Authority <= 2);
                if (!citesApprovedCopy)
                    failures.Add("product_claim_citation: product claims must cite approved copy or the reference guide (authority 1-2)");
            }
        }

        if (claims is { Compliant: false })
            failures.Add($"claims_language: {string.Join("; ", claims.Violations)}");
        if (claims is { Degraded: true })
            warnings.Add("claims_language: check unavailable — skipped");

        return failures.Count == 0
            ? PostCheckResult.Pass(warnings, claims)
            : new PostCheckResult(false, failures, warnings, claims);
    }
}

public interface IClaimsLanguageChecker
{
    Task<ClaimsVerdict> CheckAsync(
        string question, string answer, IReadOnlyList<RetrievedDocument> sources, CancellationToken ct = default);
}

/// <summary>
/// Small-model claims-language audit against the approved copy (authority 1–2
/// sources only — §3: products.json is the legal-approved claims corpus).
/// Best-effort: on failure it degrades to a warning, never a failure.
/// </summary>
public sealed class AgentClaimsLanguageChecker(AIAgent agent) : IClaimsLanguageChecker
{
    public async Task<ClaimsVerdict> CheckAsync(
        string question, string answer, IReadOnlyList<RetrievedDocument> sources, CancellationToken ct = default)
    {
        var approvedCopy = sources.Where(s => s.Authority <= 2).ToList();
        if (approvedCopy.Count == 0)
            return new ClaimsVerdict { Compliant = true }; // nothing approved to check against
        try
        {
            return await StructuredCall.RunAsync<ClaimsVerdict>(
                agent, Prompts.BuildClaimsUserMessage(question, answer, approvedCopy),
                "dotfit_claims", Schemas.Claims, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ClaimsVerdict { Compliant = true, Degraded = true, Evidence = [$"check failed: {e.Message}"] };
        }
    }
}
