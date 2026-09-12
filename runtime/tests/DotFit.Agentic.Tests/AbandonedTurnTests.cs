using DotFit.Agentic.Service;
using DotFit.Agentic.Turn;

namespace DotFit.Agentic.Tests;

/// <summary>
/// A client that hangs up mid-stream (design §9, §10). With nothing gated, the
/// turn log is the only account of what an audience saw — so the turn nobody
/// read is exactly the one that must not vanish from it.
/// </summary>
public class AbandonedTurnTests
{
    private sealed class NullWriter : ISseWriter
    {
        public Task WriteAsync(string eventName, object payload, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingTurnSink : ITurnSink
    {
        public List<TurnLog> Logs { get; } = [];
        public void Write(TurnLog log) => Logs.Add(log);
    }

    /// <summary>Emits one event, then behaves as if the caller went away.</summary>
    private sealed class HangingAssistant : IAgenticAssistant
    {
        public async IAsyncEnumerable<TurnEvent> AskAsync(
            AskRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TurnStageEvent(Stages.Thinking);
            await Task.Yield();
            throw new OperationCanceledException(ct);
        }
    }

    [Fact]
    public async Task An_abandoned_turn_still_writes_one_log_line()
    {
        var sink = new RecordingTurnSink();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var options = AgenticServiceOptions.FromValues(
            new Dictionary<string, string> { [AgenticServiceOptions.ApiKeyVar] = "k" }, supportContact: null);

        await AskStream.RunAsync(
            new HangingAssistant(),
            new AskBody { Question = "q", RequestId = "turn-9" },
            new NullWriter(), options, sink, NullTranscriptSink.Instance, cts.Token);

        TurnLog log = Assert.Single(sink.Logs);
        Assert.Equal(TurnLog.OutcomeAbandoned, log.Outcome);
        Assert.Equal("turn-9", log.RequestId);
        // Zeros are a statement that no result was reached, not a claim about
        // what the turn would have said.
        Assert.Equal(0, log.SourceCount);
        Assert.Empty(log.Queries);
    }
}
