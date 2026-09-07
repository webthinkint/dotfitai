using System.Text.Json;

namespace DotFit.Agents.Structured;

/// <summary>
/// Hand-authored strict schemas (required + additionalProperties:false), the
/// Stage 2 style. Field names are snake_case and the .NET types carry the
/// matching JsonPropertyName attributes.
///
/// The <c>*Json</c> constants are the source of truth; each is parsed exactly
/// once into the <see cref="JsonElement"/> the call sites pass. Parsing per
/// call would rent pooled buffers on every request and never return them —
/// <see cref="JsonDocument"/> releases only on Dispose, and the element has to
/// outlive the call that uses it.
/// </summary>
public static class Schemas
{
    public const string GuardrailJson = """
        {
          "type": "object",
          "properties": {
            "escalate": { "type": "boolean" },
            "reasons": { "type": "array", "items": { "type": "string", "enum": [
              "pregnancy_or_breastfeeding", "managed_condition", "eating_disorder",
              "under_18", "medication_interaction", "extreme_calorie_target",
              "self_harm", "other"
            ] } },
            "claim_trap": { "type": "boolean" },
            "notes": { "type": "string" }
          },
          "required": ["escalate", "reasons", "claim_trap", "notes"],
          "additionalProperties": false
        }
        """;

    public const string RewriteJson = """
        {
          "type": "object",
          "properties": {
            "canonical_question": { "type": "string" },
            "product_mentions": { "type": "array", "items": { "type": "string" } },
            "topics": { "type": "array", "items": { "type": "string" } },
            "confidence": { "type": "number" }
          },
          "required": ["canonical_question", "product_mentions", "topics", "confidence"],
          "additionalProperties": false
        }
        """;

    public const string ClaimsJson = """
        {
          "type": "object",
          "properties": {
            "compliant": { "type": "boolean" },
            "violations": { "type": "array", "items": { "type": "string" } },
            "evidence": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["compliant", "violations", "evidence"],
          "additionalProperties": false
        }
        """;

    public static readonly JsonElement Guardrail = Parse(GuardrailJson);
    public static readonly JsonElement Rewrite = Parse(RewriteJson);
    public static readonly JsonElement Claims = Parse(ClaimsJson);

    // The JsonDocument is deliberately not disposed: it owns the element's
    // backing memory for the lifetime of the process.
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
