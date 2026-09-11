using System.Text.Json.Serialization;
using DotFit.Agents.Structured;
using Microsoft.Agents.AI;

namespace DotFit.Agents.Guardrails;

/// <summary>Guardrail pre-check verdict (plan §11 stage 1).</summary>
public sealed class GuardrailVerdict
{
    [JsonPropertyName("escalate")] public bool Escalate { get; set; }
    [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = [];
    [JsonPropertyName("claim_trap")] public bool ClaimTrap { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    /// <summary>
    /// True when the verdict rests on something an earlier turn said rather
    /// than on the question itself (open item 19) — "I'm 14" three turns back,
    /// then "how much creatine?". Recorded because it is the only way to tell a
    /// conversation-level catch from a single-turn one, which is what the §12
    /// multi-turn set scores and what open item 20's per-request log needs. It
    /// changes nothing the customer sees: the refusal is the same handoff.
    /// </summary>
    [JsonPropertyName("history_trigger")] public bool HistoryTrigger { get; set; }
    /// <summary>
    /// What kind of turn this is — one of <see cref="ConversationIntents"/>.
    /// Not every turn a customer types is a question about a product: "Hi
    /// there" and "thanks!" are conversation, and the §11 pipeline used to run
    /// the whole retrieval chain over them and then fail the greeting on
    /// <c>citation_presence</c>, because a greeting cites nothing and the
    /// vector search returns its <c>top</c> neighbours whatever the query says.
    /// Under <c>Gated</c> that turned "Hi there" into the support handoff.
    ///
    /// Defaults to <see cref="ConversationIntents.Question"/>, which is the
    /// behavior every caller had before this field existed: an unknown,
    /// missing or degraded intent takes the full retrieval path.
    /// </summary>
    [JsonPropertyName("intent")] public string Intent { get; set; } = ConversationIntents.Question;
    /// <summary>True when the check could not run (API/parse failure). Never blocks the pipeline.</summary>
    public bool Degraded { get; set; }

    public IReadOnlyList<string> DisplayReasons =>
        Reasons.Select(r => EscalationReasons.Display.TryGetValue(r, out var d) ? d : r).ToList();

    /// <summary>
    /// Whether this turn takes the conversational branch — no retrieval, no
    /// grounded answer, no citation contract to honour.
    ///
    /// Every precedence rule lives here, in one expression, so the pipeline and
    /// the post-check cannot disagree about what a conversational turn is:
    ///
    /// - <see cref="Escalate"/> wins outright. "Hi! I'm 14, what should I
    ///   take?" is a greeting *and* a hard-escalation trigger, and it refuses.
    /// - <see cref="ClaimTrap"/> wins too: a presumed disease claim has to be
    ///   corrected from approved copy, which needs the retrieval path.
    /// - A <see cref="Degraded"/> check never routes a turn off the normal
    ///   path. The guardrail fails open, and failing open means falling back to
    ///   the fully checked route, not to the one with no sources in it.
    /// - Only <see cref="ConversationIntents.SmallTalk"/> branches.
    ///   <see cref="ConversationIntents.OutOfScope"/> is classified and logged
    ///   but keeps the retrieval path it already has — the §12 adversarial
    ///   out-of-scope items are delivered today (<c>n_withheld: 0</c>), so
    ///   there is nothing there to fix and a reroute would only put a working
    ///   redirect at risk.
    /// </summary>
    public bool Conversational =>
        !Escalate && !ClaimTrap && !Degraded && Intent == ConversationIntents.SmallTalk;
}

/// <summary>
/// What kind of turn the customer typed (§11 guardrail intent). The vocabulary
/// is fixed here and mirrored in <see cref="Structured.Schemas.GuardrailJson"/>;
/// anything else a model returns is read as <see cref="Question"/>, because the
/// full retrieval path is the safe default and an unrecognised intent is not a
/// reason to skip it.
/// </summary>
public static class ConversationIntents
{
    /// <summary>A question the corpus might answer — the normal §11 path.</summary>
    public const string Question = "question";
    /// <summary>Greetings, thanks, sign-offs, "what can you do?" — no retrieval.</summary>
    public const string SmallTalk = "smalltalk";
    /// <summary>Orders, shipping, returns, accounts, careers — dotFIT support's, not ours.</summary>
    public const string OutOfScope = "out_of_scope";

    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { Question, SmallTalk, OutOfScope };

    /// <summary>The intent as one of <see cref="Known"/>, or <see cref="Question"/>.</summary>
    public static string Normalize(string? intent) =>
        intent is not null && Known.Contains(intent) ? intent : Question;
}

/// <summary>Reason codes ↔ customer-facing phrasing for the refusal template.</summary>
public static class EscalationReasons
{
    public static readonly IReadOnlyDictionary<string, string> Display = new Dictionary<string, string>
    {
        ["pregnancy_or_breastfeeding"] = "pregnancy or breastfeeding",
        ["managed_condition"] = "a managed medical condition",
        ["eating_disorder"] = "eating-disorder concerns",
        ["under_18"] = "guidance for someone under 18",
        ["medication_interaction"] = "prescription-medication interactions",
        ["extreme_calorie_target"] = "an extreme calorie target",
        ["self_harm"] = "self-harm concerns",
        ["other"] = "a question that needs a clinician",
    };
}

public interface IGuardrail
{
    /// <summary>
    /// <paramref name="history"/> is the earlier turns of this conversation,
    /// oldest first and already trimmed by
    /// <see cref="ConversationHistory.Normalize"/>. Like the rewriter's it has
    /// no default, and for a sharper reason: a caller that forgets to pass it
    /// gets a safety check that cannot see "I'm 14" (open item 19). Pass
    /// <c>[]</c> to judge a question standalone.
    /// </summary>
    Task<GuardrailVerdict> CheckAsync(
        string question, IReadOnlyList<ConversationTurn> history, CancellationToken ct = default);
}

/// <summary>
/// Small-model guardrail pre-check. Fails open *by design*: when the check
/// itself is unavailable, the answer agent's instructions still carry the full
/// hard-escalation policy and the post-check still verifies it — a dead
/// pre-check must not brick the assistant (the alternative, blocking every
/// question on a transient API error, is worse for a support tool).
///
/// It judges the question **in its conversation** (open item 19). A hard
/// escalation trigger is a fact about the customer, not a property of the
/// sentence that carried it, and a customer states it once: "I'm 14" and "how
/// much creatine?" are two turns of one question, and reading the second alone
/// answers a minor. The prompt holds the other line too — history is context
/// for the question being asked, not a second question to answer — so an
/// earlier turn cannot put the rest of the conversation behind a refusal.
/// </summary>
public sealed class AgentGuardrail(AIAgent agent) : IGuardrail
{
    public async Task<GuardrailVerdict> CheckAsync(
        string question, IReadOnlyList<ConversationTurn> history, CancellationToken ct = default)
    {
        try
        {
            GuardrailVerdict verdict = await StructuredCall.RunAsync<GuardrailVerdict>(
                agent, Answering.Prompts.BuildGuardrailUserMessage(question, history),
                "dotfit_guardrail", Schemas.Guardrail, ct).ConfigureAwait(false);
            // The schema constrains the enum, but the value decides whether a
            // turn skips retrieval — so it is re-checked here rather than
            // trusted, the same way VerdictLog re-checks the reason codes.
            verdict.Intent = ConversationIntents.Normalize(verdict.Intent);
            return verdict;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new GuardrailVerdict
            {
                Escalate = false,
                Reasons = [],
                ClaimTrap = false,
                HistoryTrigger = false,
                // A degraded check takes the full path, never the branch: see
                // GuardrailVerdict.Conversational.
                Intent = ConversationIntents.Question,
                Notes = $"guardrail degraded: {e.Message}",
                Degraded = true,
            };
        }
    }
}
