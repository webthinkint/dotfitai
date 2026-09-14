using System.Text.Json;
using DotFit.Agents.Cost;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agents.Structured;

/// <summary>Structured output failed (bad JSON, null, or unusable model reply).</summary>
public sealed class StructuredCallException(string message) : Exception(message);

/// <summary>
/// Strict JSON-schema calls over an <see cref="AIAgent"/>, the same contract
/// Stage 2 proves on gpt-5-mini (chat_smoke.py): the schema is hand-authored
/// (required + additionalProperties:false) and deserialization happens here,
/// in our code, where it is testable. No temperature is ever passed — GPT-5
/// deployments only accept the default; determinism comes from the schema.
/// </summary>
public static class StructuredCall
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <paramref name="meter"/> is the per-request cost meter, optional and
    /// additive. Every <c>StructuredCall</c> site in this codebase is a
    /// small-deployment stage (guardrail, rewrite, claims audit), so usage is
    /// recorded on the meter's small-chat tier; a main-deployment structured
    /// call would need to say so before this default stops being right.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        AIAgent agent, string userText, string schemaName, JsonElement schema,
        CancellationToken ct = default, TurnMeter? meter = null)
    {
        var options = new AgentRunOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, schemaName, null),
        };
        AgentResponse response = await agent.RunAsync(userText, null, options, ct).ConfigureAwait(false);
        // Observed, additive: the usage the deployment reported on this call,
        // toward the turn's cost block. Recorded even when the JSON below
        // fails to parse — the tokens were spent either way, and that is
        // exactly the spend the owners are reading.
        if (response.Usage is { } usage)
            meter?.ChatSmall(usage.InputTokenCount, usage.CachedInputTokenCount, usage.OutputTokenCount);
        string text = response.Text ?? "";
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, Web);
        }
        catch (JsonException e)
        {
            throw new StructuredCallException($"{schemaName}: model returned invalid JSON ({e.Message})");
        }
        return value ?? throw new StructuredCallException($"{schemaName}: model returned null");
    }
}
