using System.Text.Json;
using System.Text.Json.Serialization;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;

namespace DotFit.Agents.Cli;

/// <summary>
/// The machine-readable projection of one <see cref="AssistantResult"/> —
/// <c>dotfit-agent ask --json</c>, and the input contract the §12 eval harness
/// consumes over a subprocess boundary.
///
/// It is an *explicit* projection, not a reflection dump of the library types:
/// the harness contract must not drift when a library record gains a field or
/// a computed property. Names are snake_case to match the pipeline's JSON
/// artifacts, and both answer texts are emitted — <c>answer_text</c> is the
/// generated draft (present even when gated withheld it, for §12 scoring) and
/// <c>delivered_text</c> is what the caller actually saw. Source
/// <c>content</c> rides along because the RAGAS-style faithfulness and context
/// precision metrics score the answer against the retrieved context.
///
/// Timings are included but are the one non-deterministic field; nothing in
/// the harness may key on them.
/// </summary>
public static class AskJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    public static string Serialize(AssistantResult result, AnswerStreamMode mode) =>
        JsonSerializer.Serialize(Project(result, mode), Options);

    private static object Project(AssistantResult r, AnswerStreamMode mode) => new
    {
        Question = r.Question,
        StreamMode = mode.ToString(),
        Escalated = r.Escalated,
        Withheld = r.Withheld,
        Guardrail = Project(r.Guardrail),
        Rewrite = Project(r.Rewrite),
        Expansion = Project(r.Expansion),
        Sources = r.Sources.Select((d, i) => Project(d, i + 1)).ToList(),
        AnswerText = r.AnswerText,
        DeliveredText = r.DeliveredText,
        Citations = r.Citations.Select(Project).ToList(),
        RenderedCitations = r.RenderedCitations,
        PostCheck = Project(r.PostCheck),
        StageSeconds = r.StageSeconds.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)),
    };

    private static object Project(GuardrailVerdict v) => new
    {
        Escalate = v.Escalate,
        Reasons = v.Reasons,
        ClaimTrap = v.ClaimTrap,
        Notes = v.Notes,
        Degraded = v.Degraded,
    };

    private static object Project(RewriteResult w) => new
    {
        CanonicalQuestion = w.CanonicalQuestion,
        ProductMentions = w.ProductMentions,
        Topics = w.Topics,
        Confidence = w.Confidence,
        Degraded = w.Degraded,
        DegradedReason = w.DegradedReason,
    };

    private static object Project(AliasExpansion e) => new
    {
        SearchTerms = e.SearchTerms,
        Families = e.Families,
        PartNos = e.PartNos,
        Notes = e.Notes,
    };

    private static object Project(RetrievedDocument d, int rank) => new
    {
        Rank = rank,
        Id = d.Id,
        SourceType = d.SourceType,
        Authority = d.Authority,
        Title = d.Title,
        Content = d.Content,
        CitationUrl = d.CitationUrl,
        Locator = d.Locator,
        Products = d.Products,
        Topics = d.Topics,
        Date = d.Date?.ToString("yyyy-MM-dd"),
        IsCurrent = d.IsCurrent,
        ProductStatus = d.ProductStatus,
        Score = Math.Round(d.Score, 5),
        RerankerScore = d.RerankerScore is { } s ? Math.Round(s, 3) : (double?)null,
        BoostedScore = Math.Round(d.BoostedScore, 5),
    };

    private static object Project(Citation c) => new
    {
        Index = c.Index,
        SourceId = c.SourceId,
        Title = c.Title,
        Line = c.Line,
    };

    private static object Project(PostCheckResult p) => new
    {
        Passed = p.Passed,
        Failures = p.Failures,
        Warnings = p.Warnings,
        Claims = p.Claims is null ? null : new
        {
            Compliant = p.Claims.Compliant,
            Violations = p.Claims.Violations,
            Evidence = p.Claims.Evidence,
            Degraded = p.Claims.Degraded,
            Skipped = p.Claims.Skipped,
        },
    };
}
