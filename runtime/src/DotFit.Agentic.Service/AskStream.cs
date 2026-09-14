using DotFit.Agents.Cost;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotFit.Agentic.Turn;
using DotFit.Agents;

namespace DotFit.Agentic.Service;

/// <summary>The wire body of <c>POST /ask</c> (design §9).</summary>
public sealed record AskBody
{
    public string Question { get; init; } = "";

    /// <summary>
    /// Absent means "new conversation", which is what controls the disclosure.
    /// The service holds no state either way — this is a flag, not a handle.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>The caller's id for this turn. Echoed on <c>result</c> and <c>error</c>.</summary>
    public string? RequestId { get; init; }

    public IReadOnlyList<Turn>? History { get; init; }

    public int? Top { get; init; }

    public sealed record Turn
    {
        public string Role { get; init; } = "";
        public string Text { get; init; } = "";
    }
}

public interface ISseWriter
{
    Task WriteAsync(string eventName, object payload, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// Maps one turn of <see cref="IAgenticAssistant"/> onto the SSE contract
/// (design §9). Transport only — no decision is made here.
///
/// Three differences from v1's stream, all consequences of decision D3:
///
/// - **Deltas stream live.** Nothing is buffered, because nothing downstream
///   can fail and retract them. That is the latency this branch was built for.
/// - **There is no <c>retraction</c> event.** Nothing is withheld, so there is
///   nothing to retract. The name is retired rather than reused.
/// - **There is a <c>source</c> event**, emitted as each source is numbered and
///   before any delta that could cite it (§7). A client can therefore resolve
///   <c>[3]</c> the moment it arrives instead of waiting for <c>result</c>.
///
/// What the caller is sent is narrower than what the CLI shows: no source
/// <c>content</c>, no tool-call arguments beyond the stage detail. The
/// endpoint is private to the website backend — but the wire stays narrow
/// anyway: retrieved text and tool arguments are operator diagnostics, and
/// nothing on this side needs them to render a turn.
/// </summary>
public static class AskStream
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string EventDisclosure = "disclosure";
    public const string EventStage = "stage";
    public const string EventSource = "source";
    public const string EventDelta = "delta";
    public const string EventResult = "result";
    public const string EventError = "error";

    public static async Task RunAsync(
        IAgenticAssistant assistant,
        AskBody body,
        ISseWriter writer,
        AgenticServiceOptions options,
        ITurnSink turns,
        ITranscriptSink transcripts,
        CancellationToken ct)
    {
        string requestId = Trim(body.RequestId) ?? Guid.NewGuid().ToString("n");
        var request = new AskRequest
        {
            Question = body.Question.Trim(),
            History = ParseHistory(body.History),
            Top = body.Top,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        string outcome = TurnLog.OutcomeAbandoned;
        string? errorKind = null;
        TurnResult? result = null;
        var sources = new List<SourceRef>();
        var sourceIds = new List<string>();

        try
        {
            // Once, and only for a conversation the caller did not give an id
            // for. Rendered before any answer text.
            if (Trim(body.ConversationId) is null)
            {
                await writer.WriteAsync(EventDisclosure,
                    new { text = DotFit.Agentic.Prompting.SystemPrompt.ConversationDisclosure() }, ct)
                    .ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }

            await foreach (TurnEvent turnEvent in assistant.AskAsync(request, timeout.Token).ConfigureAwait(false))
            {
                switch (turnEvent)
                {
                    case TurnStageEvent stage:
                        await writer.WriteAsync(EventStage,
                            new { stage = stage.Stage, detail = stage.Detail }, ct).ConfigureAwait(false);
                        break;

                    case TurnSourceEvent source:
                        sources.Add(source.Source);
                        sourceIds.Add(source.Source.Id);
                        await writer.WriteAsync(EventSource, Wire(source.Source), ct).ConfigureAwait(false);
                        break;

                    case TurnDeltaEvent delta:
                        await writer.WriteAsync(EventDelta, new { text = delta.Text }, ct).ConfigureAwait(false);
                        break;

                    case TurnErrorEvent error:
                        outcome = TurnLog.OutcomeError;
                        errorKind = error.Kind;
                        // The message is the operator's, not the caller's: it
                        // can carry a deployment name or an Azure error body.
                        await writer.WriteAsync(EventError,
                            new { request_id = requestId, kind = error.Kind, message = "the assistant failed" },
                            ct).ConfigureAwait(false);
                        break;

                    case TurnResultEvent finished:
                        result = finished.Result;
                        if (outcome != TurnLog.OutcomeError)
                            outcome = TurnLog.OutcomeAnswered;
                        await writer.WriteAsync(EventResult, Wire(finished.Result, requestId), ct)
                            .ConfigureAwait(false);
                        break;
                }
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up. Nothing to write to, and nothing was read.
            outcome = TurnLog.OutcomeAbandoned;
        }
        catch (OperationCanceledException)
        {
            // Our own request timeout, not the caller's abort: the connection
            // is still open and the response has been 200 since the first
            // frame, so the only honest ending is the one every other failure
            // gets — `error`, the handoff as a delta, then `result` last. The
            // filtered catch above must not swallow this: without this branch
            // the exception left RunAsync, the stream ended with no terminal
            // event (breaking "`result` is always last"), and the turn logged
            // `abandoned` — the operator view saying the customer left when in
            // fact the service gave up.
            outcome = TurnLog.OutcomeError;
            errorKind = "timeout";
            result = await FailAsync(
                writer, requestId,
                DotFit.Agentic.Prompting.SystemPrompt.HandoffMessage(options.SupportContact),
                sources, clock.ElapsedMilliseconds, ct).ConfigureAwait(false);
        }
        finally
        {
            // Written from a finally so every terminal path logs — with nothing
            // gated, this record is the only account of what an audience saw
            // (§8.3). A client that hangs up mid-stream produces no result, and
            // that turn is exactly the one worth knowing about, so it logs an
            // empty one rather than nothing.
            turns.Write(result is not null
                ? TurnLog.From(result, outcome, requestId, errorKind, request.History.Count)
                : Abandoned(requestId, request.History.Count));

            if (result is not null)
                transcripts.Write(requestId, request.Question, result, sourceIds);
        }
    }

    /// <summary>
    /// The terminal pair for a failure the loop could not emit for itself:
    /// <c>error</c>, the handoff as a <c>delta</c>, then <c>result</c> — the
    /// same shape and the same order <see cref="IAgenticAssistant"/> uses, so a
    /// client needs no second code path. Returns the result it wrote, or null
    /// if the connection went away while writing it (in which case the turn was
    /// abandoned after all, and the log says so).
    /// </summary>
    private static async Task<TurnResult?> FailAsync(
        ISseWriter writer,
        string requestId,
        string handoff,
        IReadOnlyList<SourceRef> sources,
        long elapsedMs,
        CancellationToken ct)
    {
        var result = new TurnResult
        {
            AnswerText = handoff,
            // What was numbered before the ceiling fired. The customer may have
            // seen citations to them, so they are part of the record.
            Sources = sources,
            ToolCalls = [],
            FirstDeltaMs = elapsedMs,
            TotalMs = elapsedMs,
            BudgetExhausted = false,
            CitedSources = [],
            Families = [],
        };
        try
        {
            // The message stays generic for the same reason the loop's does:
            // the detail is the operator's, and this one names our own limits.
            await writer.WriteAsync(EventError,
                new { request_id = requestId, kind = "timeout", message = "the assistant failed" }, ct)
                .ConfigureAwait(false);
            await writer.WriteAsync(EventDelta, new { text = handoff }, ct).ConfigureAwait(false);
            await writer.WriteAsync(EventResult, Wire(result, requestId), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The turn nobody read. Counts are zero because the loop never reached a
    /// result — an abandoned line is a statement that a request arrived and
    /// ended without one, not a claim about what it would have said.
    /// </summary>
    private static TurnLog Abandoned(string requestId, int historyTurns) => new()
    {
        RequestId = requestId,
        Outcome = TurnLog.OutcomeAbandoned,
        ToolCalls = 0,
        ToolsUsed = new Dictionary<string, int>(),
        Queries = [],
        BudgetExhausted = false,
        SourceCount = 0,
        CitedCount = 0,
        CitedAuthorities = [],
        Families = [],
        FirstDeltaMs = 0,
        TotalMs = 0,
        HistoryTurns = historyTurns,
    };

    private static object Wire(SourceRef source) => new
    {
        n = source.N,
        source_type = source.SourceType,
        authority = source.Authority,
        title = source.Title,
        citation_url = source.CitationUrl,
        locator = source.Locator,
        quotable = source.Quotable,
    };

    private static object Wire(TurnResult result, string requestId) => new
    {
        request_id = requestId,
        answer = result.AnswerText,
        sources = result.Sources.Select(Wire).ToArray(),
        cited = result.CitedSources,
        tool_calls = result.ToolCalls.Count,
        budget_exhausted = result.BudgetExhausted,
        first_delta_ms = result.FirstDeltaMs,
        total_ms = result.TotalMs,
        // Null only on the service-timeout handoff, where the loop's meter was
        // lost with the cancelled turn — omitted rather than guessed. The shape
        // is `TurnCost.ToWire()`, shared with v1's service so an A/B caller
        // reads one cost schema.
        cost = result.Cost?.ToWire(),
    };

    private static IReadOnlyList<ConversationTurn> ParseHistory(IReadOnlyList<AskBody.Turn>? history)
    {
        if (history is null || history.Count == 0)
            return ConversationHistory.Empty;
        var turns = new List<ConversationTurn>(history.Count);
        foreach (AskBody.Turn turn in history)
        {
            // Rejected already by AgenticServiceOptions.Reject — an unknown
            // role never reaches here, and dropping the turn silently would
            // change what the conversation says.
            ConversationHistory.TryParseRole(turn.Role, out ConversationRole role);
            turns.Add(new ConversationTurn(role, turn.Text ?? ""));
        }
        return turns;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
