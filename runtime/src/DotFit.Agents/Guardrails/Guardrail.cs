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
    Task<GuardrailVerdict> CheckAsync(string question, CancellationToken ct = default);
}

/// <summary>
/// Small-model guardrail pre-check. Fails open *by design*: when the check
/// itself is unavailable, the answer agent's instructions still carry the full
/// hard-escalation policy and the post-check still verifies it — a dead
/// pre-check must not brick the assistant (the alternative, blocking every
/// question on a transient API error, is worse for a support tool).
/// </summary>
public sealed class AgentGuardrail(AIAgent agent) : IGuardrail
{
    public async Task<GuardrailVerdict> CheckAsync(string question, CancellationToken ct = default)
    {
        try
        {
            return await StructuredCall.RunAsync<GuardrailVerdict>(
                agent, question, "dotfit_guardrail", Schemas.Guardrail, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new GuardrailVerdict
            {
                Escalate = false,
                Reasons = [],
                ClaimTrap = false,
                Notes = $"guardrail degraded: {e.Message}",
                Degraded = true,
            };
        }
    }
}
