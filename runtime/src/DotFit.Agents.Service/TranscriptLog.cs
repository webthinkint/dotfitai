using System.Text.Json;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;

namespace DotFit.Agents.Service;

/// <summary>
/// One structured record of what the service **said** about one request —
/// the debug companion to <see cref="VerdictLog"/>, and the deliberate
/// exception to its ruling (owner ruling, 2026-09-11, recorded in
/// <c>docs/decisions.md</c> Runtime §11).
///
/// The verdict log's rule — "what was decided and never what was said" — was
/// priced against *public* traffic, where the caller's database is the system
/// of record and a second copy of the conversation is a new PII surface. The
/// preview is a different audience: only stakeholders test the service, the
/// owner ruled preview PII acceptable, and retractions — a withheld draft, a
/// `claims_language` failure that quotes the wording it rejected — cannot be
/// debugged from a log that holds neither the draft nor the message. So this
/// record carries exactly what <see cref="VerdictLog"/> filters away: the raw
/// question, the history as received, the draft answer *including a withheld
/// one*, the delivered text, the retraction reason, the guardrail's
/// free-prose notes and the full post-check failure messages.
///
/// The price is paid visibly and reversibly, not quietly eroded:
///
/// - **Opt-in, and off by default** — <see cref="ServiceOptions.DebugTranscript"/>
///   (<c>DOTFIT_SERVICE_DEBUG_TRANSCRIPT</c>). Off is the public-traffic
///   posture, so forgetting this exists leaves the §4 posture intact.
/// - **Loud when on** — a boot line on stdout and a <c>debug_transcript</c>
///   field on <c>/healthz</c>, for the same reason <c>auth</c> is there: an
///   operator must be able to see the posture a deployment is actually in.
/// - **A separate record, not a widened one** — <see cref="VerdictLog"/> is
///   unchanged and keeps every guarantee it ever had, so turning this off
///   restores the previous state with nothing to clean up.
///
/// It must be **off before public customer traffic** — that is the ruling's
/// scope, and it is a gate on the same line as items 12/17, not a suggestion.
///
/// Like the verdict log, one line per accepted request from a <c>finally</c> on
/// every terminal path, to stdout (the journal already collects it), tagged
/// <c>"log":"dotfit.transcript"</c>. A <c>400</c>/<c>401</c> logs nothing: it
/// never reached the pipeline and the caller sees it synchronously.
/// </summary>
public sealed record TranscriptLog
{
    public const string SchemaName = "dotfit.transcript";
    /// <summary>1.1.0 added the §11 stage 6b pre-repair draft and its verdict.</summary>
    public const string SchemaVersion = "1.1.0";

    public string Log { get; init; } = SchemaName;
    public string LogVersion { get; init; } = SchemaVersion;

    public required string RequestId { get; init; }
    public string? ConversationId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    // --- the words — the fields VerdictLog structurally cannot hold ---------

    /// <summary>The question exactly as received, not the rewrite.</summary>
    public required string Question { get; init; }
    /// <summary>The history exactly as received, before <c>ConversationHistory</c> trims it.</summary>
    public IReadOnlyList<WireTurn> History { get; init; } = [];
    /// <summary>The rewrite stage's standalone question, null on paths that never rewrote.</summary>
    public string? CanonicalQuestion { get; init; }
    /// <summary>The generated draft — including one that was withheld; that is the retraction.</summary>
    public string? AnswerText { get; init; }
    /// <summary>
    /// The draft as first written, when a §11 stage 6b repair pass replaced it.
    /// This is the reason that log exists, one layer further in: the verdict log
    /// can now say a request was <c>repaired</c>, but "repaired" is only
    /// debuggable next to the sentence that was cut and the audit's reason for
    /// cutting it — and a repair that quietly deletes a correct answer looks
    /// identical, in every other field, to one that did its job.
    /// </summary>
    public string? PreRepairAnswerText { get; init; }
    /// <summary>What the caller actually received (the handoff, on a withheld answer).</summary>
    public string? DeliveredText { get; init; }
    public bool Withheld { get; init; }
    /// <summary>The retraction frame's reason — the joined failure messages, verbatim.</summary>
    public string? Retraction { get; init; }
    /// <summary>Raw reason codes, not folded to the display vocabulary — this is the debug view.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public bool Escalated { get; init; }
    public bool HistoryTrigger { get; init; }
    public bool ClaimTrap { get; init; }
    /// <summary>The guardrail's intent as it arrived, not normalized.</summary>
    public string Intent { get; init; } = ConversationIntents.Question;
    /// <summary>
    /// The guardrail's free-prose notes — deliberately absent from
    /// <see cref="VerdictLog"/> because they come from a model; here they are
    /// the point.
    /// </summary>
    public string? Notes { get; init; }
    public bool GuardrailDegraded { get; init; }
    public bool RewriteDegraded { get; init; }

    // --- post-check, messages included -----------------------------------------

    public bool PostCheckPassed { get; init; }
    /// <summary>Full failure messages — the text <see cref="VerdictLog"/> keeps only the name of.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> ClaimsViolations { get; init; } = [];
    public IReadOnlyList<string> ClaimsEvidence { get; init; } = [];

    /// <summary>The full failure messages that earned the repair pass — empty when none ran.</summary>
    public IReadOnlyList<string> PreRepairFailures { get; init; } = [];
    /// <summary>The wording the audit rejected in the first draft — what stage 6b was asked to fix.</summary>
    public IReadOnlyList<string> PreRepairClaimsViolations { get; init; } = [];
    public IReadOnlyList<string> PreRepairClaimsEvidence { get; init; } = [];

    // --- the retrieval shape -----------------------------------------------------

    public IReadOnlyList<SourceLine> Sources { get; init; } = [];
    public IReadOnlyList<CitationLine> Citations { get; init; } = [];

    public int DurationMs { get; init; }
    public IReadOnlyDictionary<string, int>? StageMs { get; init; }
    /// <summary>Exception type name or <c>Timeout</c>/<c>ClientClosed</c> — never a message.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>One earlier turn, as the caller sent it.</summary>
    public sealed record WireTurn(string Role, string Text);

    /// <summary>One retrieved source's identity — enough to find the chunk, not its content.</summary>
    public sealed record SourceLine(
        string Id, string SourceType, int Authority, string? Title, string? Locator);

    /// <summary>One citation marker the draft actually used.</summary>
    public sealed record CitationLine(int Index, string SourceId, string? Title);

    /// <summary>
    /// Project one finished (or failed) request. <paramref name="result"/> is
    /// <c>null</c> on every error and abandon path, where the question and the
    /// history are still the most useful thing in the log.
    /// <paramref name="retraction"/> is the retraction frame's reason, captured
    /// by <c>AskStream</c> as it passed through.
    /// </summary>
    public static TranscriptLog From(
        AskRequest request,
        string requestId,
        DateTimeOffset timestamp,
        TimeSpan duration,
        AssistantResult? result,
        string? retraction,
        string? errorKind)
    {
        var entry = new TranscriptLog
        {
            RequestId = requestId,
            ConversationId = request.ConversationId,
            Timestamp = timestamp,
            Question = request.Question,
            History = (request.History ?? [])
                .Select(t => new WireTurn(t.Role, t.Text))
                .ToList(),
            DurationMs = (int)duration.TotalMilliseconds,
            Retraction = retraction,
            ErrorKind = errorKind,
        };

        if (result is null)
            return entry;

        return entry with
        {
            CanonicalQuestion = result.Rewrite.CanonicalQuestion,
            AnswerText = result.AnswerText,
            DeliveredText = result.DeliveredText,
            Withheld = result.Withheld,
            Escalated = result.Escalated,
            Reasons = result.Guardrail.Reasons.ToList(),
            HistoryTrigger = result.Guardrail.HistoryTrigger,
            ClaimTrap = result.Guardrail.ClaimTrap,
            Intent = result.Guardrail.Intent,
            Notes = result.Guardrail.Notes.Length > 0 ? result.Guardrail.Notes : null,
            GuardrailDegraded = result.Guardrail.Degraded,
            RewriteDegraded = result.Rewrite.Degraded,
            PostCheckPassed = result.PostCheck.Passed,
            Failures = result.PostCheck.Failures.ToList(),
            Warnings = result.PostCheck.Warnings.ToList(),
            ClaimsViolations = result.PostCheck.Claims?.Violations.ToList() ?? [],
            ClaimsEvidence = result.PostCheck.Claims?.Evidence.ToList() ?? [],
            PreRepairAnswerText = result.PreRepairAnswerText,
            PreRepairFailures = result.PreRepairPostCheck?.Failures.ToList() ?? [],
            PreRepairClaimsViolations = result.PreRepairPostCheck?.Claims?.Violations.ToList() ?? [],
            PreRepairClaimsEvidence = result.PreRepairPostCheck?.Claims?.Evidence.ToList() ?? [],
            Sources = result.Sources.Select(s => new SourceLine(
                s.Id, s.SourceType, s.Authority, s.Title, s.Locator)).ToList(),
            Citations = result.Citations.Select(c => new CitationLine(
                c.Index, c.SourceId, c.Title)).ToList(),
            StageMs = result.StageSeconds.ToDictionary(
                kv => kv.Key, kv => (int)(kv.Value * 1000)),
        };
    }
}

/// <summary>Where debug transcripts go. One call per request, on every terminal path.</summary>
public interface ITranscriptSink
{
    void Write(TranscriptLog entry);
}

/// <summary>
/// One JSON object per line, to a <see cref="TextWriter"/> — stdout, beside the
/// verdict lines, because the journal already collects it and the same viewer
/// reads both.
/// </summary>
public sealed class JsonLinesTranscriptSink(TextWriter output) : ITranscriptSink
{
    private readonly object _gate = new();

    public void Write(TranscriptLog entry)
    {
        string line = JsonSerializer.Serialize(entry, AskStream.Json);
        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }
}

/// <summary>
/// The <see cref="ServiceOptions.DebugTranscript"/> = off implementation, and
/// the default: <see cref="Program"/> registers this so "off" means the words
/// are never serialized at all, not serialized and discarded.
/// </summary>
public sealed class NullTranscriptSink : ITranscriptSink
{
    public static readonly NullTranscriptSink Instance = new();
    private NullTranscriptSink() { }
    public void Write(TranscriptLog entry) { }
}
