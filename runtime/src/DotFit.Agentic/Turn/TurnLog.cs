using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotFit.Agentic.Turn;

/// <summary>
/// One structured record of what happened on one turn (design §10), written as
/// a single JSON line to stdout.
///
/// It carries more weight here than v1's verdict log did. On that branch the
/// log recorded decisions a gate had already made; on this one **nothing
/// gates** (decision D3), so the log is the third of the three mechanisms that
/// replaced the gate (§8.3) and the only reconstruction of what an audience was
/// shown. It is also the instrument for four open items at once: the latency
/// targets (§6), the guessed budgets (open item 5), the repeat-search cost of
/// not replaying tool results (open item 4), and whether the in-prompt safety
/// handling is holding (open item 1).
///
/// **No question text and no answer text, by construction** — there is no field
/// on this record to put them in, which is the same enforcement-not-promise
/// rule v1 used.
///
/// <see cref="Queries"/> is the deliberate exception and the one thing an owner
/// must rule on (open item 3): the searches the model chose are the single most
/// useful column for tuning — they are how you see a model looping on a term
/// that retrieves nothing — but they are the model's text, and a model asked
/// "is LeanMeal ok while breastfeeding?" may well search for close to that. It
/// is model text, not customer text, and that is a real distinction, but it is
/// not a guarantee. If the owner rules against it, this becomes a flag rather
/// than a redaction — a half-logged query is worse than none.
/// </summary>
public sealed record TurnLog
{
    /// <summary>Line discriminator — these share stdout with the host's own logs.</summary>
    public const string SchemaName = "dotfit.turn";

    public const string SchemaVersion = "1.0.0";

    /// <summary>The model answered, with or without having searched.</summary>
    public const string OutcomeAnswered = "answered";

    /// <summary>The turn threw and the customer got the templated handoff.</summary>
    public const string OutcomeError = "error";

    /// <summary>The client hung up before the turn finished.</summary>
    public const string OutcomeAbandoned = "abandoned";

    [JsonPropertyName("schema")] public string Schema => SchemaName;
    [JsonPropertyName("schema_version")] public string Version => SchemaVersion;
    [JsonPropertyName("ts")] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The caller's join key back to its own record of the conversation.</summary>
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }

    [JsonPropertyName("outcome")] public required string Outcome { get; init; }

    /// <summary>Set on <see cref="OutcomeError"/> only — the exception type, never its message.</summary>
    [JsonPropertyName("error_kind")] public string? ErrorKind { get; init; }

    // ---- what the model did ------------------------------------------------

    [JsonPropertyName("tool_calls")] public required int ToolCalls { get; init; }

    /// <summary>Calls per tool, e.g. <c>{"search": 3, "get_product": 1}</c>.</summary>
    [JsonPropertyName("tools_used")] public required IReadOnlyDictionary<string, int> ToolsUsed { get; init; }

    /// <summary>
    /// The search text the model chose, in call order. See the type remarks —
    /// this is the field open item 3 is about.
    /// </summary>
    [JsonPropertyName("queries")] public required IReadOnlyList<string> Queries { get; init; }

    /// <summary>True when a tool call was refused because the budget was spent (§6).</summary>
    [JsonPropertyName("budget_exhausted")] public required bool BudgetExhausted { get; init; }

    // ---- what it had to work with -----------------------------------------

    [JsonPropertyName("sources")] public required int SourceCount { get; init; }

    /// <summary>Sources the answer actually cites — not the ones it was given.</summary>
    [JsonPropertyName("cited")] public required int CitedCount { get; init; }

    /// <summary>Distinct §3 authority tiers among the cited sources, ascending.</summary>
    [JsonPropertyName("cited_authorities")] public required IReadOnlyList<int> CitedAuthorities { get; init; }

    /// <summary>
    /// Product families touched this turn. **Our** vocabulary from the §5 alias
    /// table, not the customer's words, which is what makes it loggable — and
    /// slicing by product is the first thing a claims question needs.
    /// </summary>
    [JsonPropertyName("families")] public required IReadOnlyList<string> Families { get; init; }

    // ---- how it went -------------------------------------------------------

    /// <summary>Milliseconds to the first delta. The number this branch exists for (§6).</summary>
    [JsonPropertyName("first_delta_ms")] public required long FirstDeltaMs { get; init; }

    [JsonPropertyName("total_ms")] public required long TotalMs { get; init; }
    [JsonPropertyName("history_turns")] public required int HistoryTurns { get; init; }
    [JsonPropertyName("input_tokens")] public long? InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public long? OutputTokens { get; init; }

    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, LineOptions);

    /// <summary>
    /// Build the record from a finished turn. The queries are read off the tool
    /// calls rather than tracked separately, so a tool that stops recording
    /// them stops logging them — there is no second path that could disagree.
    /// </summary>
    public static TurnLog From(
        TurnResult result,
        string outcome,
        string? requestId = null,
        string? errorKind = null,
        int historyTurns = 0)
    {
        var toolsUsed = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (ToolCallRecord call in result.ToolCalls)
            toolsUsed[call.Tool] = toolsUsed.TryGetValue(call.Tool, out int n) ? n + 1 : 1;

        var citedAuthorities = new SortedSet<int>();
        foreach (SourceRef source in result.Sources)
            if (result.CitedSources.Contains(source.N))
                citedAuthorities.Add(source.Authority);

        return new TurnLog
        {
            RequestId = requestId,
            Outcome = outcome,
            ErrorKind = errorKind,
            ToolCalls = result.ToolCalls.Count,
            ToolsUsed = toolsUsed,
            Queries = [.. result.ToolCalls.Where(c => c.Tool == Stages.Search).Select(c => c.Argument)],
            BudgetExhausted = result.BudgetExhausted,
            SourceCount = result.Sources.Count,
            CitedCount = result.CitedSources.Count,
            CitedAuthorities = [.. citedAuthorities],
            Families = result.Families,
            FirstDeltaMs = result.FirstDeltaMs,
            TotalMs = result.TotalMs,
            HistoryTurns = historyTurns,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
        };
    }
}
