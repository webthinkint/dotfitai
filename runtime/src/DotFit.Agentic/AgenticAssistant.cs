using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DotFit.Agentic.Config;
using DotFit.Agentic.Prompting;
using DotFit.Agentic.Retrieval;
using DotFit.Agentic.Tools;
using DotFit.Agentic.Turn;
using DotFit.Agents;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agentic;

/// <summary>One question, with whatever of the conversation the caller chose to send.</summary>
public sealed record AskRequest
{
    public required string Question { get; init; }

    /// <summary>
    /// Earlier turns, oldest first, excluding <see cref="Question"/>. The
    /// runtime holds no state between requests (decision D6), so this is the
    /// only conversation the model gets — and it is user/assistant text only.
    /// Tool calls and their results are *not* replayed (§6): the model
    /// re-retrieves if it needs the material again, which is what keeps an
    /// earlier turn's citation numbers from meaning anything in this one.
    /// </summary>
    public IReadOnlyList<ConversationTurn> History { get; init; } = ConversationHistory.Empty;

    /// <summary>Caller's override for the search tool's default <c>top</c>. Clamped.</summary>
    public int? Top { get; init; }
}

public interface IAgenticAssistant
{
    IAsyncEnumerable<TurnEvent> AskAsync(AskRequest request, CancellationToken ct = default);
}

/// <summary>
/// The loop (design §6): one model, one conversation, three tools, and nothing
/// before or after it.
///
/// There is no guardrail call, no query rewrite, no post-check and no repair
/// pass — that chain is v1's and it is what this branch replaced. The model
/// decides whether a turn needs retrieval at all, which is why small talk needs
/// no special case here: a greeting is simply a turn on which no tool is
/// called, and it comes back in about a second.
///
/// What this class owns is the event ordering the SSE contract depends on
/// (§9). Two rules:
///
/// - **Sources are published before the text that cites them.** Tools number
///   sources and queue their events as they run; the loop drains that queue
///   before yielding any delta. A client streaming live therefore always holds
///   source 3 by the time "[3]" arrives.
/// - **A result always ends the turn**, including after a failure. An error is
///   followed by the templated handoff and then the result carrying it, so a
///   caller has exactly one place to look for what the customer saw.
///
/// Nothing here inspects the answer. Deltas are forwarded as they arrive and
/// the text is never held back (decision D3).
/// </summary>
public sealed class AgenticAssistant : IAgenticAssistant
{
    private readonly AIAgent _agent;
    private readonly IKnowledgeSearch _search;
    private readonly IDocumentStore _store;
    private readonly AliasTable _aliases;
    private readonly AgenticOptions _options;
    private readonly string? _supportContact;

    public AgenticAssistant(
        AIAgent agent,
        IKnowledgeSearch search,
        IDocumentStore store,
        AliasTable aliases,
        AgenticOptions? options = null,
        string? supportContact = Prompts.DefaultSupportContact)
    {
        _agent = agent;
        _search = search;
        _store = store;
        _aliases = aliases;
        _options = options ?? new AgenticOptions();
        _supportContact = supportContact;
    }

    /// <summary>
    /// What the model says when a turn produced no text at all — it called
    /// tools and then stopped, or the deployment returned an empty completion.
    /// Templated for the same reason the handoff is: the answer to a model that
    /// produced nothing must not be another model call that can produce nothing.
    /// This is not a content judgement; nothing is being withheld.
    /// </summary>
    internal string EmptyAnswerMessage =>
        _supportContact is null
            ? "I couldn't put an answer together for that one. Try asking it a different way, or reach out to " +
              "the dotFIT support team."
            : $"I couldn't put an answer together for that one. Try asking it a different way, or reach the " +
              $"dotFIT support team at {_supportContact}.";

    public async IAsyncEnumerable<TurnEvent> AskAsync(
        AskRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var totalClock = Stopwatch.StartNew();
        AgenticOptions turnOptions = request.Top is int top
            ? _options with { DefaultTop = Math.Clamp(top, 1, _options.MaxTop) }
            : _options;

        var ledger = new SourceLedger(turnOptions.MaxSourceChars);
        var budget = new ToolBudget(turnOptions.MaxToolCalls, turnOptions.TurnTimeout);
        var tools = new KnowledgeTools(_search, _store, _aliases, turnOptions, ledger, budget);

        yield return new TurnStageEvent(Stages.Thinking);

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(turnOptions.HardTimeout);

        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = tools.AsTools() });
        var answer = new StringBuilder();
        long firstDeltaMs = -1;
        bool answerStageSent = false;
        long? inputTokens = null;
        long? outputTokens = null;
        TurnErrorEvent? failure = null;

        IAsyncEnumerator<AgentResponseUpdate> updates = _agent
            .RunStreamingAsync(BuildMessages(request), null, runOptions, turnCts.Token)
            .GetAsyncEnumerator(turnCts.Token);

        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await updates.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The caller went away. Not our failure and not the
                    // customer's problem — nothing further is emitted.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    failure = Failure("timeout",
                        $"the turn passed its {turnOptions.HardTimeout.TotalSeconds:0}-second ceiling");
                    break;
                }
                catch (Exception e)
                {
                    failure = Failure(e.GetType().Name, e.Message);
                    break;
                }

                if (!moved)
                    break;

                AgentResponseUpdate update = updates.Current;

                // Stage and source events queued by tools that ran since the
                // last update. Drained *before* the delta, which is the whole
                // ordering contract (§7).
                foreach (TurnEvent queued in ledger.Drain())
                    yield return queued;

                foreach (AIContent content in update.Contents)
                {
                    if (content is UsageContent usage)
                    {
                        inputTokens = usage.Details.InputTokenCount ?? inputTokens;
                        outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                    }
                }

                // Tool-result updates carry no assistant text; guarding on the
                // role keeps a future content type from leaking into the answer.
                if (update.Role == ChatRole.Tool || update.Text is not { Length: > 0 } delta)
                    continue;

                if (!answerStageSent)
                {
                    answerStageSent = true;
                    firstDeltaMs = totalClock.ElapsedMilliseconds;
                    yield return new TurnStageEvent(Stages.Answer);
                }

                answer.Append(delta);
                yield return new TurnDeltaEvent(delta);
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }

        foreach (TurnEvent queued in ledger.Drain())
            yield return queued;

        string answerText = answer.ToString().Trim();

        if (failure is not null)
        {
            yield return failure;
            yield return new TurnDeltaEvent(failure.HandoffText);
            answerText = failure.HandoffText;
        }
        else if (answerText.Length == 0)
        {
            answerText = EmptyAnswerMessage;
            yield return new TurnDeltaEvent(answerText);
        }

        if (firstDeltaMs < 0)
            firstDeltaMs = totalClock.ElapsedMilliseconds;

        yield return new TurnResultEvent(new TurnResult
        {
            AnswerText = answerText,
            Sources = ledger.Sources,
            ToolCalls = tools.Calls,
            FirstDeltaMs = firstDeltaMs,
            TotalMs = totalClock.ElapsedMilliseconds,
            BudgetExhausted = budget.Exhausted,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CitedSources = ledger.CitedIn(answerText),
            Families = tools.Families,
        });
    }

    private TurnErrorEvent Failure(string kind, string message) =>
        new(kind, message, SystemPrompt.HandoffMessage(_supportContact));

    /// <summary>
    /// History as plain turns, then the question. The bounds are ours, not the
    /// caller's (<see cref="ConversationHistory.Normalize"/>): a relay that
    /// sends a whole transcript must not turn one question into a prompt that
    /// costs more than the answer.
    /// </summary>
    private static List<ChatMessage> BuildMessages(AskRequest request)
    {
        var messages = new List<ChatMessage>();
        foreach (ConversationTurn turn in ConversationHistory.Normalize(request.History, request.Question))
        {
            messages.Add(new ChatMessage(
                turn.Role == ConversationRole.Assistant ? ChatRole.Assistant : ChatRole.User,
                turn.Text));
        }
        messages.Add(new ChatMessage(ChatRole.User, request.Question));
        return messages;
    }
}
