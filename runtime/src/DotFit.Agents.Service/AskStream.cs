using System.Text.Json;
using System.Text.Json.Serialization;
using DotFit.Agents.Answering;

namespace DotFit.Agents.Service;

/// <summary>One line-delimited SSE frame: <c>event:</c> + <c>data:</c>.</summary>
public interface ISseWriter
{
    Task WriteAsync(string eventName, object payload, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// The §11 pipeline rendered as a Server-Sent Events stream — the widget's
/// half of the contract.
///
/// Event names are the wire contract and a client may key on them:
///
/// - <c>disclosure</c> — AI-identity, emitted once at the start of a *new*
///   conversation (§11 standing behaviors). It is first on purpose: a widget
///   that renders events in arrival order then shows it before any answer
///   text, and a customer who never triggers a refusal is still told what
///   they are talking to.
/// - <c>stage</c> — pipeline progress. Streams even in <c>Gated</c> mode,
///   which is the whole reason gating is affordable: the widget has something
///   live to render while the deltas are held.
/// - <c>delta</c> — answer text. In <c>Gated</c> mode these arrive only after
///   the post-check has passed, in one burst.
/// - <c>retraction</c> — the post-check failed. In <c>Gated</c> mode no
///   <c>delta</c> was ever sent, and the handoff arrives as <c>delta</c>s
///   after it.
/// - <c>result</c> — citations, post-check verdict, timings. Always last.
/// - <c>error</c> — the pipeline threw. Terminal.
///
/// **The service is <c>Gated</c> and does not offer a choice.** Plan §11 fixes
/// this: streaming deltas live means a <c>claims_language</c> failure cannot
/// retract text the customer has already read, so the customer-facing surface
/// holds the answer whole. <c>Live</c> exists for the CLI and the eval
/// harness, which read the draft deliberately.
///
/// What crosses the wire is deliberately narrower than the CLI's
/// <c>--json</c>: no retrieved source <c>content</c>, and never the withheld
/// draft. Those are diagnostics for an operator, and this is a public
/// endpoint.
///
/// **Multi-turn** (open item 18): the caller sends recent turns as
/// <see cref="AskRequest.History"/> with each question, because its database
/// is the system of record and this service holds nothing between requests.
/// History is shown to the rewrite stage only, and never to the answer agent —
/// see <see cref="ConversationHistory"/>. It does not yet reach the guardrail
/// (open item 19), so safety is still judged one turn at a time.
/// </summary>
public static class AskStream
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string EventDisclosure = "disclosure";
    public const string EventStage = "stage";
    public const string EventDelta = "delta";
    public const string EventRetraction = "retraction";
    public const string EventResult = "result";
    public const string EventError = "error";

    public static async Task RunAsync(
        IKnowledgeAssistant assistant,
        AskRequest request,
        ISseWriter writer,
        CancellationToken ct)
    {
        // A request with no conversation id is a new conversation.
        if (request.ConversationId is null)
            await writer.WriteAsync(EventDisclosure,
                new { text = Prompts.ConversationDisclosure() }, ct).ConfigureAwait(false);

        // A malformed history is a 400 before the stream opens (see Program),
        // so by here it either parses or there is none to parse.
        request.TryReadHistory(out IReadOnlyList<ConversationTurn> history, out _);

        var options = new AskOptions
        {
            StreamMode = AnswerStreamMode.Gated,   // §11 — not negotiable here
            Top = request.Top,
            History = history,
        };

        try
        {
            await foreach (AssistantEvent e in
                           assistant.AskStreamAsync(request.Question, options, ct)
                               .ConfigureAwait(false))
            {
                switch (e)
                {
                    case StageEvent s:
                        await writer.WriteAsync(EventStage,
                            new { stage = s.Stage, detail = s.Detail }, ct).ConfigureAwait(false);
                        break;
                    case DeltaEvent d:
                        await writer.WriteAsync(EventDelta, new { text = d.Text }, ct)
                            .ConfigureAwait(false);
                        break;
                    case RetractionEvent x:
                        await writer.WriteAsync(EventRetraction,
                            new { reason = x.Reason, mode = x.Mode.ToString() }, ct)
                            .ConfigureAwait(false);
                        break;
                    case ResultEvent r:
                        await writer.WriteAsync(EventResult, Project(r.Result), ct)
                            .ConfigureAwait(false);
                        break;
                }
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;                                  // the client hung up
        }
        catch (Exception e)
        {
            // The customer gets the same templated handoff a failed post-check
            // produces — never an exception message, never a bare stream end.
            await writer.WriteAsync(EventError,
                new { message = "The assistant could not complete that request.",
                      kind = e.GetType().Name }, ct).ConfigureAwait(false);
            await writer.WriteAsync(EventDelta,
                new { text = Prompts.WithheldMessage() }, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The public shape of a finished run. Sources carry their identity and
    /// citation link but **not** their <c>content</c>, and the withheld draft
    /// never appears — this endpoint is customer-facing.
    /// </summary>
    private static object Project(AssistantResult r) => new
    {
        question = r.Question,
        escalated = r.Escalated,
        withheld = r.Withheld,
        answer = r.DeliveredText,
        citations = r.Citations.Select(c => new
        {
            index = c.Index, source_id = c.SourceId, title = c.Title,
        }).ToList(),
        rendered_citations = r.RenderedCitations,
        sources = r.Sources.Select(s => new
        {
            id = s.Id, source_type = s.SourceType, authority = s.Authority,
            title = s.Title, citation_url = s.CitationUrl, locator = s.Locator,
        }).ToList(),
        post_check = new
        {
            passed = r.PostCheck.Passed,
            // Failure *reasons* stay server-side: they name the check that
            // fired and, for claims_language, quote the offending wording.
            n_failures = r.PostCheck.Failures.Count,
        },
    };
}

/// <summary>One customer question. <c>ConversationId</c> absent = new conversation.</summary>
public sealed record AskRequest
{
    public string Question { get; init; } = "";
    public string? ConversationId { get; init; }
    public int? Top { get; init; }

    /// <summary>
    /// Earlier turns of this conversation, oldest first, **not including**
    /// <see cref="Question"/> (open item 18). The caller's database is the
    /// system of record — the service stores nothing between requests — so a
    /// multi-turn client resends the recent transcript it already holds.
    /// Absent or empty is a standalone question, which is what every request
    /// was before this landed.
    /// </summary>
    public IReadOnlyList<AskHistoryTurn>? History { get; init; }

    /// <summary>
    /// Map the wire history onto <see cref="ConversationTurn"/>, or explain
    /// why it cannot be mapped.
    ///
    /// An unrecognised <c>role</c> is rejected rather than dropped: dropping a
    /// turn silently changes what the conversation says, and the rewrite would
    /// then resolve a follow-up against a transcript with a hole in it. The
    /// caller gets a 400 and a fixable message instead.
    /// </summary>
    public bool TryReadHistory(out IReadOnlyList<ConversationTurn> history, out string? error)
    {
        history = ConversationHistory.Empty;
        error = null;
        if (History is null || History.Count == 0)
            return true;

        var turns = new List<ConversationTurn>(History.Count);
        for (int i = 0; i < History.Count; i++)
        {
            AskHistoryTurn turn = History[i];
            ConversationRole role;
            switch (turn.Role?.Trim().ToLowerInvariant())
            {
                case "user": role = ConversationRole.User; break;
                case "assistant": role = ConversationRole.Assistant; break;
                default:
                    error = $"history[{i}].role must be \"user\" or \"assistant\"";
                    return false;
            }
            turns.Add(new ConversationTurn(role, turn.Text ?? ""));
        }
        history = turns;
        return true;
    }
}

/// <summary>One earlier turn on the wire. <c>role</c> is <c>user</c> or <c>assistant</c>.</summary>
public sealed record AskHistoryTurn
{
    public string Role { get; init; } = "";
    public string Text { get; init; } = "";
}
