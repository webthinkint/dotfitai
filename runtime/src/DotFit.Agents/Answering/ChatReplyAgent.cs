using Microsoft.Agents.AI;

namespace DotFit.Agents.Answering;

/// <summary>
/// The conversational reply for the §11 smalltalk branch (small deployment) —
/// a greeting, a thank-you, or "what can you do?", answered without retrieval.
///
/// Deliberately **not** streamed, unlike <see cref="IAnswerAgent"/>. The reply
/// is one or two sentences, and buffering it buys the thing the branch needs
/// most: a failed call can still fall back to
/// <see cref="Prompts.SmallTalkMessage"/>, which is impossible once half a
/// greeting has been emitted. It also makes the branch behave identically under
/// <see cref="AnswerStreamMode.Live"/> and <see cref="AnswerStreamMode.Gated"/>,
/// so the CLI and the service cannot diverge on a path with no post-check
/// verdict to reconcile them.
/// </summary>
public interface IChatReplyAgent
{
    /// <summary>The reply text, or <c>null</c> when the call failed and the caller should template.</summary>
    Task<string?> ReplyAsync(string userMessage, CancellationToken ct = default);
}

/// <inheritdoc cref="IChatReplyAgent" />
public sealed class AgentChatReplyAgent(AIAgent agent) : IChatReplyAgent
{
    public async Task<string?> ReplyAsync(string userMessage, CancellationToken ct = default)
    {
        try
        {
            AgentResponse response = await agent.RunAsync(userMessage, null, null, ct).ConfigureAwait(false);
            string text = (response.Text ?? "").Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Best-effort, like every other small-model call in the pipeline:
            // an unavailable model degrades to the template, never to an error.
            return null;
        }
    }
}
