using Microsoft.Extensions.AI;

namespace DotFit.Agents.Tests;

/// <summary>
/// Scripted IChatClient: pops the next queued reply per call. Records every
/// call (messages + options) so tests can assert on what the agent sent —
/// including the structured-output ResponseFormat. No network, ever.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public sealed record RecordedCall(IList<ChatMessage> Messages, ChatOptions? Options);

    private readonly Queue<string> _replies;

    public List<RecordedCall> Calls { get; } = [];

    public ScriptedChatClient(params string[] replies) => _replies = new Queue<string>(replies);

    public ScriptedChatClient(params IEnumerable<string>[] repliesPerCall)
        => _replies = new Queue<string>(repliesPerCall.SelectMany(x => x));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        Calls.Add(new RecordedCall(messages.ToList(), options));
        if (_replies.Count == 0)
            throw new InvalidOperationException("ScriptedChatClient ran out of scripted replies");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies.Dequeue())));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls.Add(new RecordedCall(messages.ToList(), options));
        if (_replies.Count == 0)
            throw new InvalidOperationException("ScriptedChatClient ran out of scripted replies");
        string reply = _replies.Dequeue();
        foreach (string piece in Slice(reply))
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = { new TextContent(piece) } };
        }

        static IEnumerable<string> Slice(string text)
        {
            for (int i = 0; i < Math.Min(text.Length, 24); i += 8)
                yield return text.Substring(i, Math.Min(8, text.Length - i));
            if (text.Length > 24)
                yield return text[24..];
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    void IDisposable.Dispose() { }
}
