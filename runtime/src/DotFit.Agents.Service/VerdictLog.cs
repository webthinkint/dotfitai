using System.Text.Json;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;

namespace DotFit.Agents.Service;

/// <summary>
/// One structured record of what the service **decided** about one request
/// (plan §11, progress open item 20).
///
/// It exists because of the item 21 ruling: the stakeholder preview may ship at
/// any state, so this log is the only reconstruction of what that audience was
/// actually shown. It is also how open items 12 and 17 eventually get
/// production numbers instead of dev-sweep ones — the claims outcome and the
/// citation shape are exactly the columns those two metrics are computed from.
///
/// **It carries no question and no answer text, by construction.** The caller's
/// database is the system of record for the conversation (see
/// <c>docs/v1/website-integration.md</c>); a second copy here would be a new PII
/// surface, which the §4 posture does not allow for a corpus of real customer
/// mail. The rule is enforced in three places rather than trusted:
///
/// - there is no text field on this record to put an answer in;
/// - <see cref="Reasons"/> is filtered to the fixed
///   <see cref="EscalationReasons"/> vocabulary, because
///   <see cref="GuardrailVerdict.Reasons"/> comes from a model and an
///   off-vocabulary code could be anything (<see cref="GuardrailVerdict.Notes"/>,
///   free prose, is not logged at all);
/// - <see cref="Failures"/> and <see cref="Warnings"/> keep only each check's
///   *name*, never its message — <c>claims_language: (the offending wording)</c>
///   quotes the draft back, which is the leak this field would otherwise be.
///
/// <see cref="Families"/> is the one descriptive field and is deliberate: the
/// resolved product families are **our** vocabulary from the §5 alias table,
/// not the customer's words, and slicing claims failures by product is the
/// first thing items 12 and 17 need.
///
/// Joining back to the conversation is the caller's job and needs an id it
/// holds: <see cref="RequestId"/> is echoed on the terminal <c>result</c> and
/// <c>error</c> frames, and a caller may supply its own instead.
/// </summary>
public sealed record VerdictLog
{
    /// <summary>Line discriminator — these share stdout with the host's own logs.</summary>
    public const string SchemaName = "dotfit.verdict";
    /// <summary>
    /// 1.1.0 added <c>intent</c> and the <c>chitchat</c> outcome (§11 intent
    /// branch). 1.2.0 added the <c>repaired</c> outcome, <c>claims_pre_repair</c>
    /// and <c>pre_repair_failures</c> (§11 stage 6b).
    /// </summary>
    public const string SchemaVersion = "1.2.0";

    public const string OutcomeAnswered = "answered";
    public const string OutcomeEscalated = "escalated";
    public const string OutcomeWithheld = "withheld";
    public const string OutcomeError = "error";
    /// <summary>The client hung up: nobody read whatever we had.</summary>
    public const string OutcomeAbandoned = "abandoned";
    /// <summary>
    /// A conversational turn answered off the §11 intent branch — no retrieval,
    /// no citations, so it is not an <c>answered</c> question and must not be
    /// counted as one: a run of greetings would otherwise read as a healthy
    /// answer rate with a citation rate of zero.
    /// </summary>
    public const string OutcomeChitchat = "chitchat";
    /// <summary>
    /// Answered, but only after the §11 stage 6b repair pass — the first draft
    /// failed the claims audit and one bounded edit cleared it.
    ///
    /// Counted apart for the same reason <see cref="OutcomeChitchat"/> is, and
    /// with more at stake. Folded into <see cref="OutcomeAnswered"/> these rows
    /// would read <c>claims: compliant</c> — the verdict of the *second* audit —
    /// and the flag the first one raised would be absent from the log entirely,
    /// which is precisely open item 12's precision numerator quietly deleting
    /// itself. "How many did we answer" is now <c>answered + repaired</c>, and
    /// that sum being two words instead of one is the point: a rising repair
    /// rate is the answer prompt regressing (open item 17), and it must be
    /// visible without anyone having thought to look for it.
    /// </summary>
    public const string OutcomeRepaired = "repaired";

    public const string ClaimsNotRun = "not_run";
    public const string ClaimsSkipped = "skipped";
    public const string ClaimsDegraded = "degraded";
    public const string ClaimsCompliant = "compliant";
    public const string ClaimsViolation = "violation";

    /// <summary><see cref="ErrorKind"/> for a client disconnect, which is not a fault.</summary>
    public const string ClientClosedKind = "ClientClosed";

    /// <summary>A check name we could not read off a failure message — never the message.</summary>
    private const string UnnamedCheck = "unnamed";

    /// <summary>Longest thing we will accept as a check name.</summary>
    private const int MaxCheckNameChars = 40;

    public string Log { get; init; } = SchemaName;
    public string LogVersion { get; init; } = SchemaVersion;

    public required string RequestId { get; init; }
    /// <summary>Absent on the first turn of a conversation (§11 disclosure rule).</summary>
    public string? ConversationId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>One of the <c>Outcome*</c> constants — the column to group by.</summary>
    public required string Outcome { get; init; }

    public bool Escalated { get; init; }
    /// <summary>Escalation reason codes, filtered to the known vocabulary.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
    /// <summary>
    /// The verdict rests on an earlier turn (open item 19). The one thing a
    /// complaint cannot be reconstructed without: the customer sees the same
    /// handoff whether the trigger was this turn or three turns back.
    /// </summary>
    public bool HistoryTrigger { get; init; }
    public bool ClaimTrap { get; init; }
    /// <summary>
    /// What the guardrail took the turn to be — one of
    /// <see cref="ConversationIntents"/>, filtered to that vocabulary for the
    /// same reason <see cref="Reasons"/> is. This is how the owner finds out
    /// what share of the traffic is greetings and how much is work dotFIT
    /// support owns, and it is the only visibility on
    /// <see cref="ConversationIntents.OutOfScope"/>, which is classified here
    /// but deliberately changes no behavior.
    /// </summary>
    public string Intent { get; init; } = ConversationIntents.Question;
    public bool GuardrailDegraded { get; init; }
    public bool RewriteDegraded { get; init; }

    /// <summary>The generated answer was suppressed before the caller saw it.</summary>
    public bool Withheld { get; init; }
    public bool PostCheckPassed { get; init; }
    /// <summary>Failed check names only (<c>citation_presence</c>, …).</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>One of the <c>Claims*</c> constants — open item 12's column.</summary>
    public string Claims { get; init; } = ClaimsNotRun;
    public int ClaimsViolations { get; init; }

    /// <summary>A §11 stage 6b repair pass ran. It may still have been withheld after it.</summary>
    public bool Repaired { get; init; }
    /// <summary>
    /// The claims outcome of the draft the repair replaced — <c>violation</c>
    /// whenever <see cref="Repaired"/> is true, <c>not_run</c> otherwise. The
    /// field exists so <see cref="Claims"/> can go on meaning "the verdict on
    /// what we delivered" without the earlier flag being lost: item 12 counts
    /// flags raised, and after stage 6b some of those are raised against a
    /// draft nobody received.
    /// </summary>
    public string ClaimsPreRepair { get; init; } = ClaimsNotRun;
    /// <summary>Failed check names from the pre-repair verdict — names only, as <see cref="Failures"/>.</summary>
    public IReadOnlyList<string> PreRepairFailures { get; init; } = [];

    public int NSources { get; init; }
    public int NCitations { get; init; }
    /// <summary>
    /// Distinct authority levels the answer actually cited, ascending. Whether
    /// approved copy (1–2) was cited is open item 17's citation rate.
    /// </summary>
    public IReadOnlyList<int> CitedAuthorities { get; init; } = [];
    /// <summary>Product families the §5 alias table resolved from the question.</summary>
    public IReadOnlyList<string> Families { get; init; } = [];

    /// <summary>Turns the caller sent, before our own trim (<see cref="ConversationHistory"/>).</summary>
    public int HistoryTurns { get; init; }
    public int? Top { get; init; }

    public int DurationMs { get; init; }
    /// <summary>Per-stage wall clock, absent when the run never produced a result.</summary>
    public IReadOnlyDictionary<string, int>? StageMs { get; init; }
    /// <summary>Exception type name or <c>Timeout</c>/<c>ClientClosed</c> — never a message.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>
    /// Project one finished (or failed) request. <paramref name="result"/> is
    /// <c>null</c> when the pipeline never reached its <c>ResultEvent</c>, which
    /// is every error and abandon path.
    /// </summary>
    public static VerdictLog From(
        AskRequest request,
        string requestId,
        DateTimeOffset timestamp,
        TimeSpan duration,
        AssistantResult? result,
        string? errorKind)
    {
        string outcome = errorKind switch
        {
            ClientClosedKind => OutcomeAbandoned,
            not null => OutcomeError,
            _ when result is null => OutcomeError,
            // Escalated first, then withheld: both are outcomes the branch
            // cannot produce, but the ordering says which reading wins if a
            // future path ever produces two of them at once.
            _ when result.Escalated => OutcomeEscalated,
            _ when result.Withheld => OutcomeWithheld,
            _ when result.Guardrail.Conversational => OutcomeChitchat,
            // After `Withheld`, deliberately: a repair that did not save the
            // answer is a withheld request, and reading it as `repaired` would
            // turn the failure into a success in the one column the owner
            // groups by. `Repaired` stays true on that row either way.
            _ when result.Repaired => OutcomeRepaired,
            _ => OutcomeAnswered,
        };

        var entry = new VerdictLog
        {
            RequestId = requestId,
            ConversationId = request.ConversationId,
            Timestamp = timestamp,
            Outcome = outcome,
            HistoryTurns = request.History?.Count ?? 0,
            Top = request.Top,
            DurationMs = (int)duration.TotalMilliseconds,
            ErrorKind = errorKind,
        };

        if (result is null)
            return entry;

        return entry with
        {
            Escalated = result.Escalated,
            Reasons = ReasonCodes(result.Guardrail),
            HistoryTrigger = result.Guardrail.HistoryTrigger,
            ClaimTrap = result.Guardrail.ClaimTrap,
            Intent = ConversationIntents.Normalize(result.Guardrail.Intent),
            GuardrailDegraded = result.Guardrail.Degraded,
            RewriteDegraded = result.Rewrite.Degraded,
            Withheld = result.Withheld,
            PostCheckPassed = result.PostCheck.Passed,
            Failures = CheckNames(result.PostCheck.Failures),
            Warnings = CheckNames(result.PostCheck.Warnings),
            Claims = ClaimsOutcome(result.PostCheck.Claims),
            ClaimsViolations = result.PostCheck.Claims?.Violations.Count ?? 0,
            Repaired = result.Repaired,
            ClaimsPreRepair = result.PreRepairPostCheck is null
                ? ClaimsNotRun
                : ClaimsOutcome(result.PreRepairPostCheck.Claims),
            PreRepairFailures = result.PreRepairPostCheck is null
                ? []
                : CheckNames(result.PreRepairPostCheck.Failures),
            NSources = result.Sources.Count,
            NCitations = result.Citations.Count,
            CitedAuthorities = CitedLevels(result),
            Families = result.Expansion.Families,
            StageMs = result.StageSeconds.ToDictionary(
                kv => kv.Key, kv => (int)(kv.Value * 1000)),
        };
    }

    /// <summary>
    /// Reason codes we recognise; anything else collapses to <c>other</c>. The
    /// structured schema constrains these to its enum, but the degraded path and
    /// any non-structured caller do not, and an unrecognised "code" would be
    /// free model text landing in a log that promises to hold none.
    /// </summary>
    private static IReadOnlyList<string> ReasonCodes(GuardrailVerdict verdict) =>
        verdict.Reasons
            .Select(r => EscalationReasons.Display.ContainsKey(r) ? r : "other")
            .Distinct()
            .ToList();

    /// <summary>
    /// The check name a post-check message opens with, and nothing after the
    /// colon. A message that does not start with a plain <c>snake_case</c> name
    /// is not guessed at — it becomes <c>unnamed</c>, because the fallback of
    /// keeping the whole string is exactly the text leak this method prevents.
    /// </summary>
    internal static string CheckName(string message)
    {
        int colon = message.IndexOf(':');
        string name = colon > 0 ? message[..colon] : message;
        bool plain = name.Length is > 0 and <= MaxCheckNameChars
            && name.All(c => char.IsAsciiLetterLower(c) || c == '_');
        return plain ? name : UnnamedCheck;
    }

    private static IReadOnlyList<string> CheckNames(IReadOnlyList<string> messages) =>
        messages.Select(CheckName).ToList();

    private static string ClaimsOutcome(ClaimsVerdict? claims) => claims switch
    {
        null => ClaimsNotRun,
        { Skipped: true } => ClaimsSkipped,
        { Degraded: true } => ClaimsDegraded,
        { Compliant: true } => ClaimsCompliant,
        _ => ClaimsViolation,
    };

    private static IReadOnlyList<int> CitedLevels(AssistantResult result) =>
        result.Citations
            .Where(c => c.Index >= 1 && c.Index <= result.Sources.Count)
            .Select(c => result.Sources[c.Index - 1].Authority)
            .Distinct()
            .Order()
            .ToList();
}

/// <summary>
/// Where finished verdicts go. One call per request, on every terminal path.
/// Implementations must be thread-safe and must be cheap: this runs inside the
/// request, after the last frame is written.
/// </summary>
public interface IVerdictSink
{
    void Write(VerdictLog entry);
}

/// <summary>
/// One JSON object per line, to a <see cref="TextWriter"/> — stdout in the
/// service, because a container's stdout is already collected and a file sink
/// would buy rotation, permissions and a disk-full failure mode for a record
/// that is kilobytes a day. Each line carries <c>log</c> and <c>log_version</c>
/// so a collector can pick these out of the host's own console logging.
/// </summary>
public sealed class JsonLinesVerdictSink(TextWriter output) : IVerdictSink
{
    private readonly object _gate = new();

    public void Write(VerdictLog entry)
    {
        string line = JsonSerializer.Serialize(entry, AskStream.Json);
        // Serialized outside the lock; written and flushed inside it, so two
        // concurrent requests cannot interleave halves of a line.
        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }
}
