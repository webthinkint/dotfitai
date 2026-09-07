using System.Text.Json;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Service;

namespace DotFit.Agents.Tests;

/// <summary>
/// The SSE wire contract (plan §11, "SSE stream to widget"). These pin what a
/// widget may key on and — as much — what must never reach it.
/// </summary>
public class AskStreamTests
{
    private sealed record Frame(string Event, JsonElement Data);

    private sealed class RecordingWriter : ISseWriter
    {
        public List<Frame> Frames { get; } = [];
        public int Flushes { get; private set; }

        public Task WriteAsync(string eventName, object payload, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(payload, AskStream.Json);
            Frames.Add(new Frame(eventName, JsonDocument.Parse(json).RootElement.Clone()));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct)
        {
            Flushes++;
            return Task.CompletedTask;
        }

        public List<string> Names => Frames.Select(f => f.Event).ToList();
        public JsonElement? First(string name) =>
            Frames.FirstOrDefault(f => f.Event == name)?.Data;
    }

    /// <summary>Replays a scripted event sequence, or throws.</summary>
    private sealed class FakeAssistant(params AssistantEvent[] events) : IKnowledgeAssistant
    {
        public Exception? Throw { get; init; }
        public List<AskOptions?> Options { get; } = [];

        public async IAsyncEnumerable<AssistantEvent> AskStreamAsync(
            string question, AskOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Options.Add(options);
            await Task.Yield();
            if (Throw is not null)
                throw Throw;
            foreach (AssistantEvent e in events)
                yield return e;
        }
    }

    private static AssistantResult Result(
        string answer = "Grounded answer [1].", bool withheld = false,
        bool escalated = false, bool passed = true)
    {
        RetrievedDocument source = TestDocs.Product();
        return new AssistantResult
        {
            Question = "q?",
            Guardrail = new GuardrailVerdict { Escalate = escalated },
            Rewrite = new RewriteResult { CanonicalQuestion = "q?" },
            Expansion = AliasExpansion.Empty,
            Sources = [source],
            AnswerText = answer,
            DeliveredText = withheld ? Prompts.WithheldMessage() : answer,
            Citations = [new Citation(1, source.Id, source.Title, "[1]")],
            RenderedCitations = "[1] Test Product",
            PostCheck = passed
                ? PostCheckResult.Pass([], null)
                : new PostCheckResult(false, ["claims_language: bad wording"], [], null),
            StageSeconds = new Dictionary<string, double> { ["answer"] = 1.0 },
        };
    }

    private static async Task<RecordingWriter> Run(
        FakeAssistant assistant, AskRequest? request = null)
    {
        var writer = new RecordingWriter();
        await AskStream.RunAsync(
            assistant, request ?? new AskRequest { Question = "q?" }, writer,
            CancellationToken.None);
        return writer;
    }

    [Fact]
    public async Task NewConversationOpensWithTheAiDisclosure()
    {
        // §11 standing behavior: the customer is told what they are talking to
        // before any answer text, not only inside a refusal.
        RecordingWriter writer = await Run(new FakeAssistant(
            new DeltaEvent("Hi"), new ResultEvent(Result())));

        Assert.Equal(AskStream.EventDisclosure, writer.Names[0]);
        string text = writer.First(AskStream.EventDisclosure)!.Value
            .GetProperty("text").GetString()!;
        Assert.Contains("AI assistant", text);
        Assert.Contains("not medical advice", text);
    }

    [Fact]
    public async Task ContinuingConversationDoesNotRepeatTheDisclosure()
    {
        RecordingWriter writer = await Run(
            new FakeAssistant(new ResultEvent(Result())),
            new AskRequest { Question = "q?", ConversationId = "abc" });

        Assert.DoesNotContain(AskStream.EventDisclosure, writer.Names);
    }

    [Fact]
    public async Task ServiceAlwaysRunsGatedRegardlessOfTheRequest()
    {
        // §11: the customer-facing surface releases the answer whole or not at
        // all. There is deliberately no client-selectable mode.
        var assistant = new FakeAssistant(new ResultEvent(Result()));
        await Run(assistant);

        Assert.Equal(AnswerStreamMode.Gated, assistant.Options[0]!.StreamMode);
    }

    [Fact]
    public async Task StageEventsStreamSoAGatedWidgetHasSomethingLive()
    {
        RecordingWriter writer = await Run(new FakeAssistant(
            new StageEvent("guardrail", "clear"),
            new StageEvent("search", "8 sources"),
            new ResultEvent(Result())));

        Assert.Equal(2, writer.Names.Count(n => n == AskStream.EventStage));
        Assert.Equal("guardrail",
            writer.First(AskStream.EventStage)!.Value.GetProperty("stage").GetString());
        Assert.True(writer.Flushes > 0);   // buffered frames are not "live"
    }

    [Fact]
    public async Task ResultCarriesCitationsAndSourceLinks()
    {
        RecordingWriter writer = await Run(new FakeAssistant(new ResultEvent(Result())));
        JsonElement result = writer.First(AskStream.EventResult)!.Value;

        Assert.Equal("Grounded answer [1].", result.GetProperty("answer").GetString());
        Assert.Equal(1, result.GetProperty("citations").GetArrayLength());
        JsonElement source = result.GetProperty("sources")[0];
        Assert.Equal("product", source.GetProperty("source_type").GetString());
        Assert.False(string.IsNullOrEmpty(source.GetProperty("citation_url").GetString()));
    }

    [Fact]
    public async Task SourceContentNeverCrossesTheWire()
    {
        // The CLI's --json ships retrieved content for the eval harness. This
        // endpoint is public: a widget gets identity and a link, not corpus text.
        RecordingWriter writer = await Run(new FakeAssistant(new ResultEvent(Result())));
        JsonElement source = writer.First(AskStream.EventResult)!.Value
            .GetProperty("sources")[0];

        Assert.False(source.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task WithheldAnswerDeliversTheHandoffAndNeverTheDraft()
    {
        const string draft = "Take 5 g of creatine daily for your kidney condition.";
        RecordingWriter writer = await Run(new FakeAssistant(
            new RetractionEvent("claims_language", AnswerStreamMode.Gated),
            new DeltaEvent(Prompts.WithheldMessage()),
            new ResultEvent(Result(answer: draft, withheld: true, passed: false))));

        string wire = string.Join("\n", writer.Frames.Select(f => f.Data.GetRawText()));
        Assert.DoesNotContain("5 g of creatine", wire);
        Assert.Contains(AskStream.EventRetraction, writer.Names);
        JsonElement result = writer.First(AskStream.EventResult)!.Value;
        Assert.True(result.GetProperty("withheld").GetBoolean());
        Assert.Contains("support team", result.GetProperty("answer").GetString()!);
    }

    [Fact]
    public async Task PostCheckFailureReasonsStaySeverSide()
    {
        // "claims_language: <the offending wording>" is an operator diagnostic;
        // echoing it tells a prober exactly which phrasing tripped the audit.
        RecordingWriter writer = await Run(new FakeAssistant(
            new ResultEvent(Result(withheld: true, passed: false))));
        JsonElement postCheck = writer.First(AskStream.EventResult)!.Value
            .GetProperty("post_check");

        Assert.False(postCheck.GetProperty("passed").GetBoolean());
        Assert.Equal(1, postCheck.GetProperty("n_failures").GetInt32());
        Assert.False(postCheck.TryGetProperty("failures", out _));
    }

    [Fact]
    public async Task PipelineFailureDeliversTheHandoffNotAnExceptionMessage()
    {
        var assistant = new FakeAssistant()
        {
            Throw = new InvalidOperationException("connection string 'secret' is bad"),
        };
        RecordingWriter writer = await Run(assistant);

        string wire = string.Join("\n", writer.Frames.Select(f => f.Data.GetRawText()));
        Assert.DoesNotContain("secret", wire);
        Assert.Contains(AskStream.EventError, writer.Names);
        // the customer still gets a human handoff, not a silent dead stream
        Assert.Contains("support team",
            writer.Frames.Last(f => f.Event == AskStream.EventDelta)
                .Data.GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task ClientDisconnectPropagatesRatherThanBeingSwallowed()
    {
        var assistant = new FakeAssistant() { Throw = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(assistant));
    }

    [Fact]
    public void RequestsBindSnakeCaseLikeTheResponsesEmitIt()
    {
        // The wire is snake_case in both directions. When it was not, an
        // unbound conversation_id made every turn look new and re-sent the
        // disclosure — a silent failure, since binding does not complain.
        AskRequest? request = JsonSerializer.Deserialize<AskRequest>(
            """{"question":"q?","conversation_id":"c1","top":5}""", AskStream.Json);

        Assert.NotNull(request);
        Assert.Equal("c1", request!.ConversationId);
        Assert.Equal(5, request.Top);
    }

    [Fact]
    public void PayloadsAreSnakeCaseAndNewlineSafe()
    {
        // An SSE frame ends at a blank line, so a raw newline in the payload
        // would truncate it. JSON escaping is what keeps the frame intact.
        string json = JsonSerializer.Serialize(
            new { some_text = "line one\nline two" }, AskStream.Json);
        Assert.DoesNotContain("\n", json);
        Assert.Contains("\\n", json);
    }
}
