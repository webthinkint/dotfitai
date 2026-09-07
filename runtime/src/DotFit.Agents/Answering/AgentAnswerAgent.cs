using Microsoft.Agents.AI;

namespace DotFit.Agents.Answering;

/// <summary>
/// The grounded-answer agent (plan §11 stage 4, chat deployment). Streaming is
/// the primary shape — the SSE service later maps deltas to stream events.
/// </summary>
public interface IAnswerAgent
{
    IAsyncEnumerable<AgentResponseUpdate> StreamAsync(string userMessage, CancellationToken ct = default);
}

public sealed class AgentAnswerAgent(AIAgent agent) : IAnswerAgent
{
    public async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(
        string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(userMessage, null, null, ct)
                           .WithCancellation(ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
