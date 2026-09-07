using System.Text.Json;
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

    public static async Task<T> RunAsync<T>(
        AIAgent agent, string userText, string schemaName, string schemaJson, CancellationToken ct = default)
    {
        var options = new AgentRunOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonDocument.Parse(schemaJson).RootElement, schemaName, null),
        };
        AgentResponse response = await agent.RunAsync(userText, null, options, ct).ConfigureAwait(false);
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
