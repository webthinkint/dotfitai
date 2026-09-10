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
    /// <summary>True when the check could not run (API/parse failure). Never blocks the pipeline.</summary>
    public bool Degraded { get; set; }

    public IReadOnlyList<string> DisplayReasons =>
        Reasons.Select(r => EscalationReasons.Display.TryGetValue(r, out var d) ? d : r).ToList();
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
            return await StructuredCall.RunAsync<GuardrailVerdict>(
                agent, Answering.Prompts.BuildGuardrailUserMessage(question, history),
                "dotfit_guardrail", Schemas.Guardrail, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new GuardrailVerdict
            {
                Escalate = false,
                Reasons = [],
                ClaimTrap = false,
                HistoryTrigger = false,
                Notes = $"guardrail degraded: {e.Message}",
                Degraded = true,
            };
        }
    }
}
