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

/// <summary>
/// When the caller may see the answer text (plan §11, "streaming vs. gating").
/// <see cref="Live"/> emits deltas as the model produces them, so a failed
/// post-check can only be reported after the text has been read — the
/// diagnostic mode, and the CLI default. <see cref="Gated"/> withholds every
/// delta until the post-check has run and releases the answer only on PASS —
/// the customer-facing mode, and the SSE service default.
/// </summary>
public enum AnswerStreamMode
{
    Live,
    Gated,
}

/// <summary>Per-question options for the assistant pipeline.</summary>
public sealed record AskOptions
{
    public bool ClaimsCheck { get; init; } = true;
    public int? Top { get; init; }
    public bool? Semantic { get; init; }
    public string? Filter { get; init; }
    public AnswerStreamMode StreamMode { get; init; } = AnswerStreamMode.Live;

    /// <summary>
    /// Earlier turns of this conversation, oldest first, **excluding** the
    /// question being asked (open item 18). The runtime keeps no state between
    /// requests, so a multi-turn caller resends the recent transcript it
    /// already owns; the pipeline trims it through
    /// <see cref="ConversationHistory.Normalize"/> and shows it to the rewrite
    /// stage only.
    /// </summary>
    public IReadOnlyList<ConversationTurn> History { get; init; } = ConversationHistory.Empty;
}

/// <summary>Streamed pipeline events (the SSE service maps these later).</summary>
public abstract record AssistantEvent;

/// <summary>A pipeline stage completed (guardrail / rewrite / aliases / search / answer / post-check).</summary>
public sealed record StageEvent(string Stage, string Detail) : AssistantEvent;

/// <summary>A streamed answer fragment.</summary>
public sealed record DeltaEvent(string Text) : AssistantEvent;

/// <summary>
/// The answer did not survive the post-check. On <see cref="AnswerStreamMode.Gated"/>
/// nothing has been emitted yet and the delta that follows is the handoff
/// message; on <see cref="AnswerStreamMode.Live"/> the caller has already seen
/// the text and must retract what it rendered. Either way the failing text is
/// kept in <see cref="AssistantResult.AnswerText"/> for tracing and §12 eval.
/// </summary>
public sealed record RetractionEvent(string Reason, AnswerStreamMode Mode) : AssistantEvent;

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
    /// <summary>
    /// What the caller actually received — the concatenated deltas. Equal to
    /// <see cref="AnswerText"/> except on a gated post-check failure, where the
    /// answer is withheld and the handoff message is delivered instead.
    /// </summary>
    public required string DeliveredText { get; init; }
    public required IReadOnlyList<Citation> Citations { get; init; }
    public required string RenderedCitations { get; init; }
    public required PostCheckResult PostCheck { get; init; }
    public required IReadOnlyDictionary<string, double> StageSeconds { get; init; }
    public bool Escalated => Guardrail.Escalate;
    /// <summary>True when the generated answer was suppressed before the caller saw it.</summary>
    public bool Withheld => DeliveredText != AnswerText;
}

/// <summary>
/// The §11 pipeline as one service — guardrail pre-check (small model) →
/// query rewrite (small model) + deterministic alias expansion (§5 artifact) →
/// hybrid search on the §9 index (is_current filter, authority re-rank, optional
/// semantic ranker) → grounded answer with citations (chat model, streaming) →
/// deterministic post-check (+ optional claims-language audit). No tool calls:
/// retrieval is single-shot by design in v1.
/// </summary>
public interface IKnowledgeAssistant
{
    /// <summary>The §11 pipeline as a stream of stage/delta/retraction/result events.</summary>
    IAsyncEnumerable<AssistantEvent> AskStreamAsync(
        string question, AskOptions? options = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IKnowledgeAssistant" />
public sealed class KnowledgeAssistant : IKnowledgeAssistant
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

        // Trimmed once, here, so the CLI and the SSE service cannot disagree
        // about how much history a question is allowed to carry (item 18).
        IReadOnlyList<ConversationTurn> history =
            ConversationHistory.Normalize(options.History, question);

        // --- stage 1: guardrail pre-check (small model) ------------------------
        // Deliberately still judged on this turn alone: history reaches the
        // rewrite and nothing else until open item 19 wires it here, which is
        // its own row because an escalation trigger can arrive turns before the
        // question it applies to ("I'm 14" ... "how much creatine?") and the
        // 10/10 escalation accuracy in §12 is a single-turn number.
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
                DeliveredText = refusal,
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
        RewriteResult rewrite = await _rewriter.RewriteAsync(question, history, ct).ConfigureAwait(false);
        timings["rewrite"] = sw.Elapsed.TotalSeconds;
        yield return new StageEvent("rewrite",
            $"{(rewrite.Degraded ? "(degraded) " : "")}canonical: {rewrite.CanonicalQuestion}" +
            (rewrite.ProductMentions.Count > 0
                ? $" | mentions: {string.Join(", ", rewrite.ProductMentions)}" : "") +
            (history.Count > 0 ? $" | history: {history.Count} turn(s)" : ""));

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

        bool gated = options.StreamMode == AnswerStreamMode.Gated;
        yield return new StageEvent("answer",
            $"{(gated ? "generating (gated)" : "streaming")} from {sources.Count} source(s)");

        sw.Restart();
        var answerText = new System.Text.StringBuilder();
        await foreach (AgentResponseUpdate update in _answer.StreamAsync(userMessage, ct).ConfigureAwait(false))
        {
            if (update.Text is { Length: > 0 } delta)
            {
                answerText.Append(delta);
                // Gated: the deltas are held until the post-check has run. Stage
                // events still flow, so the caller has something live to render.
                if (!gated)
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

        // --- stage 7: release or withhold -----------------------------------------
        // The checks that matter are terminal by construction — citation markers
        // are only known at the last delta, and the claims audit is the only check
        // that catches claim wording lifted from a CONTEXT ONLY source. So there is
        // no partial gate to run: the answer is either released whole or withheld
        // whole. A gated failure delivers the handoff message and never the text;
        // a live failure can only retract what the caller already rendered.
        string delivered = answer;
        if (!postCheck.Passed)
        {
            yield return new RetractionEvent(string.Join("; ", postCheck.Failures), options.StreamMode);
            if (gated)
            {
                delivered = Prompts.WithheldMessage();
                yield return new DeltaEvent(delivered);
            }
        }
        else if (gated && answer.Length > 0)
        {
            yield return new DeltaEvent(answer);
        }

        IReadOnlyList<Citation> citations = CitationFormatter.Collect(answer, sources);
        yield return new ResultEvent(new AssistantResult
        {
            Question = question,
            Guardrail = verdict,
            Rewrite = rewrite,
            Expansion = expansion,
            Sources = sources,
            AnswerText = answer,
            DeliveredText = delivered,
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

    /// <summary>
    /// Append the alias terms the question does not already say. Each term is
    /// judged against the *question* only, never against the terms already
    /// appended: two families can be word-subsumed ("Whey Protein" inside "Best
    /// Whey Protein") and dropping the second would lose a resolved family from
    /// the BM25 query.
    /// </summary>
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
