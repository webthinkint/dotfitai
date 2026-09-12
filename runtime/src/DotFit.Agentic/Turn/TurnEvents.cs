namespace DotFit.Agentic.Turn;

/// <summary>
/// Stage names the caller may key on (design §9). Deliberately a short closed
/// list: the SSE contract says event names and stage names are the contract and
/// the prose is not, so a new stage is a documented change, not a string
/// someone typed at a call site.
/// </summary>
public static class Stages
{
    /// <summary>The model is composing — emitted once, when the first text arrives.</summary>
    public const string Answer = "answer";

    /// <summary>A <c>search</c> tool call. <c>detail</c> is the query the model chose.</summary>
    public const string Search = "search";

    /// <summary>A <c>fetch</c> tool call. <c>detail</c> is the document id.</summary>
    public const string Fetch = "fetch";

    /// <summary>A <c>get_product</c> tool call. <c>detail</c> is what was asked for.</summary>
    public const string Product = "product";

    /// <summary>The turn started; nothing has been decided yet.</summary>
    public const string Thinking = "thinking";
}

/// <summary>
/// One source as the caller sees it, identified by the number the model cites.
///
/// The number is turn-scoped and assigned when the tool result is built, not
/// when the answer is assembled (design §7). That is the whole reason
/// <c>[n]</c> survives live streaming: by the time a delta containing "[3]"
/// reaches the client, source 3 has already been sent to it.
/// </summary>
public sealed record SourceRef
{
    public required int N { get; init; }
    public required string Id { get; init; }
    public required string SourceType { get; init; }
    public required int Authority { get; init; }
    public required string Title { get; init; }
    public string? CitationUrl { get; init; }
    public string? Locator { get; init; }
    /// <summary>§3: authority 1–2 may supply product-claim wording; 3–5 is context.</summary>
    public required bool Quotable { get; init; }
}

/// <summary>One tool call, as recorded for the turn log (§10) and the CLI trace.</summary>
public sealed record ToolCallRecord
{
    public required string Tool { get; init; }
    /// <summary>The query / id / product name the model passed. Model text, not customer text.</summary>
    public required string Argument { get; init; }
    /// <summary>Sources the call returned, new and repeat together.</summary>
    public required int ResultCount { get; init; }
    /// <summary>Sources the call returned that the turn had not already seen.</summary>
    public required int NewSourceCount { get; init; }
    public required long ElapsedMs { get; init; }
    /// <summary>Set when the call was refused — the budget was spent, or the tool failed.</summary>
    public string? Refusal { get; init; }
}

/// <summary>The assembled turn. What the CLI prints, the service returns, and the log reads.</summary>
public sealed record TurnResult
{
    public required string AnswerText { get; init; }
    public required IReadOnlyList<SourceRef> Sources { get; init; }
    public required IReadOnlyList<ToolCallRecord> ToolCalls { get; init; }
    /// <summary>Milliseconds to the first answer delta — the number this branch exists for (§6).</summary>
    public required long FirstDeltaMs { get; init; }
    public required long TotalMs { get; init; }
    /// <summary>True when the tool-call or wall-clock budget ended the turn (§6).</summary>
    public required bool BudgetExhausted { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    /// <summary>Source numbers the answer text actually cites, ascending.</summary>
    public required IReadOnlyList<int> CitedSources { get; init; }

    /// <summary>
    /// Product families the alias table resolved this turn (§5). Our vocabulary,
    /// not the customer's, which is what makes it safe to log (§10).
    /// </summary>
    public required IReadOnlyList<string> Families { get; init; }
}

/// <summary>
/// What a turn emits, in order. The stream is the primary shape — the CLI
/// renders it, the SSE service maps it one-for-one onto wire events, and the
/// tests assert on it.
///
/// Ordering is a contract, not an accident: every <see cref="TurnSourceEvent"/> for
/// a source precedes any <see cref="TurnDeltaEvent"/> that could cite it, and
/// <see cref="TurnResultEvent"/> is always last — including after an
/// <see cref="TurnErrorEvent"/>, so a caller has exactly one place to read what the
/// customer ended up seeing.
/// </summary>
public abstract record TurnEvent;

public sealed record TurnStageEvent(string Stage, string? Detail = null) : TurnEvent;

public sealed record TurnSourceEvent(SourceRef Source) : TurnEvent;

public sealed record TurnDeltaEvent(string Text) : TurnEvent;

public sealed record TurnResultEvent(TurnResult Result) : TurnEvent;

/// <summary>
/// The turn threw. <see cref="HandoffText"/> is what the customer gets, and the
/// loop emits it as a delta immediately after — there is no second model call
/// to fall back on, and a failure that answers with silence is worse than one
/// that answers with a phone number. A <see cref="TurnResultEvent"/> still follows,
/// carrying the handoff as the answer.
/// </summary>
public sealed record TurnErrorEvent(string Kind, string Message, string HandoffText) : TurnEvent;
