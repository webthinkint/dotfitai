namespace DotFit.Agents.Structured;

/// <summary>
/// Hand-authored strict schemas (required + additionalProperties:false), the
/// Stage 2 style. Field names are snake_case and the .NET types carry the
/// matching JsonPropertyName attributes.
/// </summary>
public static class Schemas
{
    public const string Guardrail = """
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

    public const string Rewrite = """
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

    public const string Claims = """
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
}
