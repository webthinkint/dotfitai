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
    /// <summary>
    /// True when the audit was never invoked, because nothing at all was
    /// retrieved. Distinct from a <c>Compliant</c> verdict it actually reached:
    /// both used to serialize as <c>compliant: true, degraded: false</c>, so a
    /// draft the audit never looked at was indistinguishable from one it
    /// cleared. On the 2026-09-10 adversarial sweep that was **10 of the 13**
    /// non-escalated items, and it silently inflated open item 12's recall
    /// denominator — the metric counted misses against an audit that never ran.
    /// Those 10 were the context-only sets, which now get audited (open item
    /// 24); this flag is left for the genuinely empty set, and for reading
    /// older run records.
    /// </summary>
    public bool Skipped { get; set; }
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
///
/// Three answer shapes reach it, and they are not scored alike: a grounded
/// answer carries the full citation contract; a refusal — the pre-check's or
/// the answer agent's own — must hand off and cite nothing; and a
/// conversational turn (§11 intent branch) has neither contract, because
/// nothing was retrieved for it. The shape is read off
/// <see cref="GuardrailVerdict"/>, never guessed from the text.
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

        // A conversational turn (§11 intent branch): nothing was retrieved, so
        // the citation and escalation checks are about a contract this answer
        // does not have. The citation ones would pass anyway — they are already
        // conditioned on a non-empty source list — but `escalation_respected`
        // would not: a greeting that names the support team reads to the
        // model-escalation heuristic below as a refusal, and every such turn
        // would then carry a warning that misreports what happened. The
        // out-of-range warning above still stands, and is the one worth having
        // here: a branch with no sources has no [n] it is allowed to write.
        //
        // The claims audit below is *not* skipped. It does not run on this path
        // today (there is nothing to audit an empty source set against), but a
        // caller that does run one gets its verdict honoured rather than
        // silently dropped on the one path with no other check in it.
        bool conversational = guardrail.Conversational && sources.Count == 0;

        bool handsOff = HandoffPhrases.Any(p => answer.Contains(p, StringComparison.OrdinalIgnoreCase));

        // A refusal the *answer agent* wrote, which the pre-check did not
        // predict: it cites nothing and hands off. This is the shape the
        // fail-open guardrail produces (plan §12 degraded-guardrail case) —
        // the pre-check degraded to escalate=false, so the answer agent's own
        // hard-escalation instruction caught it instead. Scored as the refusal
        // it is: the non-escalation branch below would fail it on
        // citation_presence, which under Gated replaces a correct refusal with
        // the handoff template and reports a defect that is not one.
        bool modelEscalated = !conversational && !guardrail.Escalate && handsOff && markers.Count == 0;
        if (modelEscalated)
            warnings.Add("escalation_respected: the answer refused and handed off without the pre-check asking for it"
                + (guardrail.Degraded ? " (pre-check was degraded)" : ""));

        if (guardrail.Escalate || modelEscalated)
        {
            // The refusal must hand off and must not hand out product guidance.
            // KnowledgeAssistant templates the pre-check's refusal, so neither
            // branch can fire from that path; they hold the line for a
            // model-written one and for any other caller (the SSE service, the
            // eval harness) that lets a model write it.
            if (!handsOff)
                failures.Add("escalation_respected: the refusal does not hand off to the support team or a healthcare professional");
            if (markers.Count > 0)
                failures.Add("escalation_respected: an escalated answer must not cite product guidance");
        }
        else if (!conversational)
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
/// Small-model claims-language audit of the draft's product claims against the
/// approved copy (§3: products.json is the legal-approved claims corpus).
///
/// It runs on any non-empty source set and judges against the whole retrieved
/// list. The §3 line travels with each source as its
/// <see cref="Prompts.ClaimsMarker"/>; withholding the context-only sources
/// instead made every claim grounded in them look unsupported (see
/// <see cref="Prompts.BuildClaimsUserMessage"/>).
///
/// It used to run <em>only</em> when approved copy was retrieved, on the
/// reasoning that with nothing quotable there was no approved wording to audit
/// against. That held only while the checker was shown authority 1–2 alone;
/// once it saw every source (2026-09-10), the skipped case became the
/// high-risk one — an answer built entirely from Q&amp;A and podcast context is
/// exactly where an unsupported product claim is most likely (A-047), and a
/// set with no QUOTABLE source is one where <em>every</em> product claim is
/// unsupported by definition. Open item 24.
///
/// Best-effort: on failure it degrades to a warning, never a failure.
/// </summary>
public sealed class AgentClaimsLanguageChecker(AIAgent agent) : IClaimsLanguageChecker
{
    public async Task<ClaimsVerdict> CheckAsync(
        string question, string answer, IReadOnlyList<RetrievedDocument> sources, CancellationToken ct = default)
    {
        if (sources.Count == 0)
            // Nothing retrieved at all: the answer agent was told to say it has
            // no sourced information, so there is nothing to audit and no list
            // to resolve its [n] against. Reported as skipped rather than as a
            // pass the audit never made — the eval harness keeps un-audited
            // drafts out of the recall denominator (open item 12).
            return new ClaimsVerdict { Compliant = true, Skipped = true };
        try
        {
            return await StructuredCall.RunAsync<ClaimsVerdict>(
                agent, Prompts.BuildClaimsUserMessage(question, answer, sources),
                "dotfit_claims", Schemas.Claims, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ClaimsVerdict { Compliant = true, Degraded = true, Evidence = [$"check failed: {e.Message}"] };
        }
    }
}
