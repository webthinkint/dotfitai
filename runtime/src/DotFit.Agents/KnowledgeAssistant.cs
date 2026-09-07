using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;
using Microsoft.Agents.AI;

namespace DotFit.Agents;

/// <summary>Per-question options for the assistant pipeline.</summary>
public sealed record AskOptions
{
    public bool ClaimsCheck { get; init; } = true;
    public int? Top { get; init; }
    public bool? Semantic { get; init; }
    public string? Filter { get; init; }
}

/// <summary>Streamed pipeline events (the SSE service maps these later).</summary>
public abstract record AssistantEvent;

/// <summary>A pipeline stage completed (guardrail / rewrite / aliases / search / answer / post-check).</summary>
public sealed record StageEvent(string Stage, string Detail) : AssistantEvent;

/// <summary>A streamed answer fragment.</summary>
public sealed record DeltaEvent(string Text) : AssistantEvent;

/// <summary>The final assembled result — always the last event.</summary>
public sealed record ResultEvent(AssistantResult Result) : AssistantEvent;

/// <summary>Everything one question produced, for rendering, tracing, and later eval.</summary>
public sealed record AssistantResult
{
    public required string Question { get; init; }
    public required GuardrailVerdict Guardrail { get; init; }
    public required RewriteResult Rewrite { get; init; }
    public required AliasExpansion Expansion { get; init; }
    public required IReadOnlyList<RetrievedDocument> Sources { get; init; }
    public required string AnswerText { get; init; }
    public required IReadOnlyList<Citation> Citations { get; init; }
    public required string RenderedCitations { get; init; }
    public required PostCheckResult PostCheck { get; init; }
    public required IReadOnlyDictionary<string, double> StageSeconds { get; init; }
    public bool Escalated => Guardrail.Escalate;
}

/// <summary>
/// The §11 pipeline as one service — guardrail pre-check (small model) →
/// query rewrite (small model) + deterministic alias expansion (§5 artifact) →
/// hybrid search on kb-main (is_current filter, authority re-rank, optional
/// semantic ranker) → grounded answer with citations (chat model, streaming) →
/// deterministic post-check (+ optional claims-language audit). No tool calls:
/// retrieval is single-shot by design in v1.
/// </summary>
public sealed class KnowledgeAssistant
{
    private readonly IGuardrail _guardrail;
    private readonly IQueryRewriter _rewriter;
    private readonly AliasTable _aliases;
    private readonly IKnowledgeSearch _search;
    private readonly IAnswerAgent _answer;
    private readonly IClaimsLanguageChecker? _claims;
    private readonly SearchSettings _settings;

    public KnowledgeAssistant(
        IGuardrail guardrail,
        IQueryRewriter rewriter,
        AliasTable aliases,
        IKnowledgeSearch search,
        IAnswerAgent answer,
        SearchSettings settings,
        IClaimsLanguageChecker? claimsChecker = null)
    {
        _guardrail = guardrail;
        _rewriter = rewriter;
        _aliases = aliases;
        _search = search;
        _answer = answer;
        _claims = claimsChecker;
        _settings = settings;
    }

    /// <summary>Full pipeline, streamed. Always ends with exactly one <see cref="ResultEvent"/>.</summary>
    public async IAsyncEnumerable<AssistantEvent> AskStreamAsync(
        string question,
        AskOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new AskOptions();
        var timings = new Dictionary<string, double>();
        var sw = Stopwatch.StartNew();

        // --- stage 1: guardrail pre-check (small model) ------------------------
        GuardrailVerdict verdict = await _guardrail.CheckAsync(question, ct).ConfigureAwait(false);
        timings["guardrail"] = sw.Elapsed.TotalSeconds;
        yield return new StageEvent("guardrail", verdict.Escalate
            ? $"ESCALATE ({string.Join(", ", verdict.Reasons)})"
            : verdict.Degraded ? "clear (degraded — check unavailable)" : "clear");

        if (verdict.Escalate)
        {
            // Deterministic refusal: no LLM call on the escalation path, so the
            // post-check can verify the handoff wording exactly.
            string refusal = Prompts.RefusalMessage(verdict.DisplayReasons);
            timings["answer"] = 0;
            yield return new DeltaEvent(refusal);
            yield return new ResultEvent(new AssistantResult
            {
                Question = question,
                Guardrail = verdict,
                Rewrite = new RewriteResult { CanonicalQuestion = question },
                Expansion = AliasExpansion.Empty,
                Sources = [],
                AnswerText = refusal,
                Citations = [],
                RenderedCitations = "",
                PostCheck = PostChecker.Check(verdict,
                    new RewriteResult { CanonicalQuestion = question }, AliasExpansion.Empty, [], refusal),
                StageSeconds = timings,
            });
            yield break;
        }

        // --- stage 2: query rewrite (small model) -------------------------------
        sw.Restart();
        RewriteResult rewrite = await _rewriter.RewriteAsync(question, ct).ConfigureAwait(false);
        timings["rewrite"] = sw.Elapsed.TotalSeconds;
        yield return new StageEvent("rewrite",
            $"{(rewrite.Degraded ? "(degraded) " : "")}canonical: {rewrite.CanonicalQuestion}" +
            (rewrite.ProductMentions.Count > 0
                ? $" | mentions: {string.Join(", ", rewrite.ProductMentions)}" : ""));

        // --- stage 3: deterministic alias expansion (§5 artifact) ----------------
        AliasExpansion expansion = _aliases.Expand(question, rewrite.ProductMentions);
        yield return new StageEvent("aliases", expansion.Families.Count > 0
            ? $"families: {string.Join(", ", expansion.Families)}" +
              (expansion.PartNos.Count > 0 ? $" · part_nos {string.Join(",", expansion.PartNos)}" : "")
            : "no product resolved");

        // --- stage 4: hybrid search ----------------------------------------------
        string searchText = JoinDistinct(rewrite.CanonicalQuestion, expansion.SearchTerms);
        bool semantic = options.Semantic ?? _settings.SemanticDefault;
        var searchParameters = new SearchParameters
        {
            QueryText = searchText,
            Top = options.Top ?? _settings.DefaultTop,
            VectorCandidates = _settings.VectorCandidates,
            Semantic = semantic,
            AdditionalFilter = options.Filter,
        };
        sw.Restart();
        IReadOnlyList<RetrievedDocument> sources =
            await _search.SearchAsync(searchParameters, ct).ConfigureAwait(false);
        timings["search"] = sw.Elapsed.TotalSeconds;
        yield return new StageEvent("search",
            $"{sources.Count} source(s) · semantic={(semantic ? "on" : "off")} · top={searchParameters.Top}");

        // --- stage 5: grounded answer (chat model, streamed) ---------------------
        var notes = new List<string>(expansion.Notes);
        if (verdict.ClaimTrap)
            notes.Add("The question presumes a claim the approved copy may not make. Do not endorse it; " +
                      "correct it politely and answer only from approved copy.");
        string userMessage = Prompts.BuildAnswerUserMessage(question, sources, notes);

        yield return new StageEvent("answer", $"streaming from {sources.Count} source(s)");

        sw.Restart();
        var answerText = new System.Text.StringBuilder();
        await foreach (AgentResponseUpdate update in _answer.StreamAsync(userMessage, ct).ConfigureAwait(false))
        {
            if (update.Text is { Length: > 0 } delta)
            {
                answerText.Append(delta);
                yield return new DeltaEvent(delta);
            }
        }
        timings["answer"] = sw.Elapsed.TotalSeconds;
        string answer = answerText.ToString();

        // --- stage 6: post-check --------------------------------------------------
        sw.Restart();
        ClaimsVerdict? claimsVerdict = null;
        if (options.ClaimsCheck && _claims is not null)
            claimsVerdict = await _claims.CheckAsync(question, answer, sources, ct).ConfigureAwait(false);
        PostCheckResult postCheck = PostChecker.Check(verdict, rewrite, expansion, sources, answer, claimsVerdict);
        timings["post-check"] = sw.Elapsed.TotalSeconds;
        yield return new StageEvent("post-check",
            postCheck.Passed ? "PASS" : $"FAIL ({string.Join("; ", postCheck.Failures)})");

        IReadOnlyList<Citation> citations = CitationFormatter.Collect(answer, sources);
        yield return new ResultEvent(new AssistantResult
        {
            Question = question,
            Guardrail = verdict,
            Rewrite = rewrite,
            Expansion = expansion,
            Sources = sources,
            AnswerText = answer,
            Citations = citations,
            RenderedCitations = CitationFormatter.RenderBlock(citations),
            PostCheck = postCheck,
            StageSeconds = timings,
        });
    }

    /// <summary>Full pipeline, non-streamed convenience (tests, eval harness).</summary>
    public async Task<AssistantResult> AskAsync(
        string question, AskOptions? options = null, CancellationToken ct = default)
    {
        AssistantResult? result = null;
        await foreach (AssistantEvent e in AskStreamAsync(question, options, ct).ConfigureAwait(false))
        {
            if (e is ResultEvent r)
                result = r.Result;
        }
        return result ?? throw new InvalidOperationException("the pipeline ended without a result");
    }

    private static string JoinDistinct(string first, IReadOnlyList<string> rest)
    {
        var parts = new List<string> { first };
        var seen = new HashSet<string>(
            first.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        foreach (string term in rest)
        {
            if (term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => !seen.Contains(w)))
                parts.Add(term);
        }
        return string.Join(" ", parts);
    }
}
