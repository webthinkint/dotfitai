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
/// The two terminal frames (<c>result</c> and <c>error</c>) carry
/// <c>request_id</c>, which is the join key between the caller's conversation
/// record and our <see cref="VerdictLog"/>. It rides in the body rather than a
/// response header because the website server relays this stream: a body field
/// survives the relay, a header is the relay's to keep or drop.
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
/// History is shown to the guardrail and the rewrite stage, and never to the
/// answer agent — see <see cref="ConversationHistory"/>. Safety is judged over
/// the conversation as of open item 19: a trigger stated in an earlier turn
/// ("I'm 14") still escalates the question that follows it.
///
/// **Hardening** (open item 22) arrives as <see cref="ServiceOptions"/>: the
/// request timeout is applied here because the two cancellation cases are not
/// the same event — a client hang-up is rethrown and writes nothing, our own
/// timeout takes the failure path, since a stream that merely stops is
/// indistinguishable from a network fault and invites the retry the caller was
/// told not to make. Auth and the length/`top` limits are checked in
/// <c>Program</c> instead, before the stream opens: once the first frame is
/// written the response is a 200 and no status code is left to reject with.
///
/// **Verdict logging** (open item 20) is the other reason the terminal paths
/// are kept distinct: exactly one <see cref="VerdictLog"/> is written per
/// request, from a <c>finally</c>, so an abandoned run is recorded as
/// abandoned rather than as nothing at all.
///
/// **Debug transcript logging** (owner ruling 2026-09-11) is its opt-in
/// companion: when the caller supplies an <see cref="ITranscriptSink"/>, one
/// <see cref="TranscriptLog"/> is written from the same <c>finally</c> with
/// the question, the withheld draft and the full failure messages — see
/// <see cref="TranscriptLog"/> for the ruling and its scope.
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
        CancellationToken ct,
        ServiceOptions? service = null,
        IVerdictSink? verdicts = null,
        ITranscriptSink? transcripts = null)
    {
        service ??= new ServiceOptions { ApiKey = null };

        // The join key for open item 20's log. The caller may bring its own —
        // its database already has a turn id, and reusing it saves a join —
        // but must not be *required* to, so one is minted when it does not.
        string requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("n")
            : request.RequestId.Trim();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        AssistantResult? result = null;
        string? errorKind = null;
        // The retraction frame's reason, captured as it passes through — the
        // joined failure messages. On the transcript it is what a retraction
        // looked like on the wire; on the verdict log it never appears.
        string? retraction = null;

        // The request timeout is ours, not the caller's (item 22). A wedged
        // upstream call would otherwise hold the connection — and the Azure
        // quota behind it — open for as long as the caller is willing to wait,
        // and the caller was told to be generous. Linked so a client hang-up
        // still cancels first, which is the case that must *not* write a
        // handoff: there is nobody left to read it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (service.RequestTimeout > TimeSpan.Zero)
            timeout.CancelAfter(service.RequestTimeout);

        try
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

            await foreach (AssistantEvent e in
                           assistant.AskStreamAsync(request.Question, options, timeout.Token)
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
                        retraction = x.Reason;
                        await writer.WriteAsync(EventRetraction,
                            new { reason = x.Reason, mode = x.Mode.ToString() }, ct)
                            .ConfigureAwait(false);
                        break;
                    case ResultEvent r:
                        result = r.Result;
                        await writer.WriteAsync(EventResult, Project(r.Result, requestId), ct)
                            .ConfigureAwait(false);
                        break;
                }
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errorKind = VerdictLog.ClientClosedKind;
            throw;                                  // the client hung up
        }
        catch (OperationCanceledException)
        {
            // Our own timeout, with the client still listening: it gets the
            // failure path, not a truncated stream, because a stream that just
            // stops looks identical to a network fault and invites the retry
            // the caller was told not to make.
            errorKind = "Timeout";
            await FailAsync(writer, errorKind, requestId, service.SupportContact, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            errorKind = e.GetType().Name;
            await FailAsync(writer, errorKind, requestId, service.SupportContact, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // Exactly one record per request, on every terminal path including
            // the rethrown hang-up (open item 20) — a run that produced no log
            // is indistinguishable from a run that never happened, which is the
            // reconstruction the item 21 ruling says must always be possible.
            Log(verdicts, request, requestId, startedAt, elapsed.Elapsed, result, errorKind);
            LogTranscript(transcripts, request, requestId, startedAt, elapsed.Elapsed,
                result, retraction, errorKind);
        }
    }

    /// <summary>
    /// Write the verdict, and never let the attempt reach the caller: a sink
    /// that cannot write is an operational problem, not a reason to fail a
    /// request that has already been answered — and in the hang-up case this
    /// runs while an <c>OperationCanceledException</c> is in flight, which a
    /// throw from here would swallow.
    /// </summary>
    private static void Log(
        IVerdictSink? verdicts, AskRequest request, string requestId,
        DateTimeOffset startedAt, TimeSpan duration, AssistantResult? result, string? errorKind)
    {
        if (verdicts is null)
            return;
        try
        {
            verdicts.Write(VerdictLog.From(request, requestId, startedAt, duration, result, errorKind));
        }
        catch (Exception)
        {
            // deliberately swallowed — see the summary above
        }
    }

    /// <summary>
    /// Write the debug transcript, with the same rules as the verdict write:
    /// the sink must never be the reason a request fails, and in the hang-up
    /// case this runs while an exception is in flight.
    /// </summary>
    private static void LogTranscript(
        ITranscriptSink? transcripts, AskRequest request, string requestId,
        DateTimeOffset startedAt, TimeSpan duration, AssistantResult? result,
        string? retraction, string? errorKind)
    {
        if (transcripts is null)
            return;
        try
        {
            transcripts.Write(TranscriptLog.From(
                request, requestId, startedAt, duration, result, retraction, errorKind));
        }
        catch (Exception)
        {
            // deliberately swallowed — same contract as the verdict sink
        }
    }

    /// <summary>
    /// The terminal failure frames: <c>error</c> with a type name and no
    /// detail, then the same templated handoff a failed post-check delivers —
    /// never an exception message, never a bare stream end.
    /// </summary>
    private static async Task FailAsync(
        ISseWriter writer, string kind, string requestId, string? supportContact, CancellationToken ct)
    {
        await writer.WriteAsync(EventError,
            new
            {
                request_id = requestId,
                message = "The assistant could not complete that request.",
                kind = kind,
            },
            ct).ConfigureAwait(false);
        await writer.WriteAsync(EventDelta,
            new { text = Prompts.WithheldMessage(supportContact) }, ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The public shape of a finished run. Sources carry their identity and
    /// citation link but **not** their <c>content</c>, and the withheld draft
    /// never appears — this endpoint is customer-facing.
    ///
    /// <c>request_id</c> is here so the caller can file the id beside the turn
    /// it stored: our <see cref="VerdictLog"/> holds the verdicts and no text,
    /// its database holds the text, and this is the key that joins them.
    /// </summary>
    private static object Project(AssistantResult r, string requestId) => new
    {
        request_id = requestId,
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
    /// The caller's own id for this turn, echoed on the terminal frames and
    /// used as the key of the verdict log (open item 20). Optional: one is
    /// minted when it is absent, so a caller that does not care is not made to
    /// care. Both this and <see cref="ConversationId"/> are length-capped,
    /// because unlike everything else on this request they are *kept* — put
    /// nothing in them that a customer said.
    /// </summary>
    public string? RequestId { get; init; }

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
            if (!ConversationHistory.TryParseRole(turn.Role, out ConversationRole role))
            {
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
