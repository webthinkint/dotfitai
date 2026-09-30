using DotFit.Assistant.Cost;
using DotFit.Assistant.Prompting;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotFit.Assistant.Turn;

/// <summary>
/// One structured record of what happened on one turn, written as
/// a single JSON line to stdout.
///
/// **Nothing gates**, so this log is, with the prompt and the tool surface,
/// one of the three things that stand in for a gate, and the only
/// reconstruction of what an audience was shown. It is also the instrument for
/// the latency targets, the tool budgets, the repeat-search cost of not
/// replaying tool results, and whether the in-prompt safety handling holds.
///
/// **No question text and no answer text, by construction** — there is no field
/// on this record to put them in.
///
/// <see cref="Queries"/> is the deliberate exception: the searches the model
/// chose are the single most useful column for tuning — they are how you see a
/// model looping on a term that retrieves nothing — but they are the model's
/// text, and a model asked "is LeanMeal ok while breastfeeding?" may well
/// search for close to that. Model text is not customer text, which is why it
/// is logged; if that ever stops being good enough, the fix is a flag that
/// drops the field, not a redaction — a half-logged query is worse than none.
/// </summary>
public sealed record TurnLog
{
    /// <summary>Line discriminator — these share stdout with the host's own logs.</summary>
    public const string SchemaName = "dotfit.turn";

    public const string SchemaVersion = "1.3.0";

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
    /// The search text the model chose, in call order. See the type remarks.
    /// </summary>
    [JsonPropertyName("queries")] public required IReadOnlyList<string> Queries { get; init; }

    /// <summary>True when a tool call was refused because the budget was spent.</summary>
    [JsonPropertyName("budget_exhausted")] public required bool BudgetExhausted { get; init; }

    // ---- what it had to work with -----------------------------------------

    [JsonPropertyName("sources")] public required int SourceCount { get; init; }

    /// <summary>Sources the answer actually cites — not the ones it was given.</summary>
    [JsonPropertyName("cited")] public required int CitedCount { get; init; }

    /// <summary>Distinct authority tiers among the cited sources, ascending.</summary>
    [JsonPropertyName("cited_authorities")] public required IReadOnlyList<int> CitedAuthorities { get; init; }

    /// <summary>
    /// Product families touched this turn. **Our** vocabulary from the alias
    /// table, not the customer's words, which is what makes it loggable — and
    /// slicing by product is the first thing a claims question needs.
    /// </summary>
    [JsonPropertyName("families")] public required IReadOnlyList<string> Families { get; init; }

    /// <summary>
    /// Reference id to version, for every reference the turn read; absent when
    /// it read none. References are uncited, so this is the only record of which
    /// revision of dotFIT's guidance shaped an answer.
    /// </summary>
    [JsonPropertyName("references")] public IReadOnlyDictionary<string, string>? References { get; init; }

    /// <summary>The prompt variant the turn ran on (<c>assistant/prompt/variants/</c>).</summary>
    [JsonPropertyName("prompt_variant")] public string? PromptVariant { get; init; }

    /// <summary><see cref="AssembledPrompt.Version"/>: which exact prompt text the turn ran on.</summary>
    [JsonPropertyName("prompt_version")] public string? PromptVersion { get; init; }

    // ---- how it went -------------------------------------------------------

    /// <summary>Milliseconds to the first delta: the latency the customer feels.</summary>
    [JsonPropertyName("first_delta_ms")] public required long FirstDeltaMs { get; init; }

    [JsonPropertyName("total_ms")] public required long TotalMs { get; init; }
    [JsonPropertyName("history_turns")] public required int HistoryTurns { get; init; }
    [JsonPropertyName("input_tokens")] public long? InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public long? OutputTokens { get; init; }

    /// <summary>
    /// The turn's cost, counts and derived money. The token
    /// counts above remain the raw pair; this carries the fuller breakdown —
    /// cached input, embedding usage, index queries — and the sheet that
    /// priced it, which is the only way a dollar figure in an old line still
    /// means anything.
    /// </summary>
    [JsonPropertyName("cost")] public TurnCost? Cost { get; init; }

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
    private static IReadOnlyDictionary<string, string>? ReferencesRead(IReadOnlyList<ToolCallRecord> calls)
    {
        var read = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (ToolCallRecord call in calls)
            if (call.Tool == Stages.Guide && call.Version is { } version)
                read[call.Argument.Split('#')[0]] = version;
        return read.Count == 0 ? null : read;
    }

    public static TurnLog From(
        TurnResult result,
        string outcome,
        string? requestId = null,
        string? errorKind = null,
        int historyTurns = 0,
        AssembledPrompt? prompt = null)
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
            References = ReferencesRead(result.ToolCalls),
            PromptVariant = prompt?.Variant,
            PromptVersion = prompt?.Version,
            FirstDeltaMs = result.FirstDeltaMs,
            TotalMs = result.TotalMs,
            HistoryTurns = historyTurns,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            Cost = result.Cost,
        };
    }
}
