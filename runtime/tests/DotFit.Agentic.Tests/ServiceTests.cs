using DotFit.Agentic.Service;
using DotFit.Agentic.Turn;
using DotFit.Agents.Config;

namespace DotFit.Agentic.Tests;

/// <summary>
/// The transport contract (design §9). These do not boot a host — they pin the
/// rules that a host cannot enforce for us: what gets a status code, what the
/// auth posture is, and which events go on the wire.
/// </summary>
public class AgenticServiceOptionsTests
{
    private static Dictionary<string, string> Env(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public void A_missing_shared_secret_fails_the_boot_rather_than_serving_an_open_endpoint()
    {
        EnvFile.EnvFileException error = Assert.Throws<EnvFile.EnvFileException>(() =>
            AgenticServiceOptions.FromValues(Env(), supportContact: null));

        Assert.Contains(AgenticServiceOptions.ApiKeyVar, error.Message, StringComparison.Ordinal);
        Assert.Contains("open endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_none_is_the_explicit_opt_out_and_must_be_said_alone()
    {
        var off = AgenticServiceOptions.FromValues(
            Env((AgenticServiceOptions.AuthVar, "none")), supportContact: null);
        Assert.True(off.AuthDisabled);
        Assert.True(off.IsAuthorized(null));

        // Saying both leaves the posture a guess, which is the thing to avoid.
        Assert.Throws<EnvFile.EnvFileException>(() => AgenticServiceOptions.FromValues(
            Env((AgenticServiceOptions.AuthVar, "none"), (AgenticServiceOptions.ApiKeyVar, "s3cret")),
            supportContact: null));
    }

    [Fact]
    public void The_shared_secret_is_checked_as_a_bearer_token_and_never_rendered()
    {
        var options = AgenticServiceOptions.FromValues(
            Env((AgenticServiceOptions.ApiKeyVar, "s3cret")), supportContact: null);

        Assert.True(options.IsAuthorized("Bearer s3cret"));
        Assert.False(options.IsAuthorized("Bearer wrong"));
        Assert.False(options.IsAuthorized("s3cret"));
        Assert.False(options.IsAuthorized(null));
        Assert.DoesNotContain("s3cret", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Debug_transcript_is_off_unless_asked_for()
    {
        var off = AgenticServiceOptions.FromValues(
            Env((AgenticServiceOptions.ApiKeyVar, "k")), supportContact: null);
        Assert.False(off.DebugTranscript);

        var on = AgenticServiceOptions.FromValues(
            Env((AgenticServiceOptions.ApiKeyVar, "k"), (AgenticServiceOptions.DebugTranscriptVar, "1")),
            supportContact: null);
        Assert.True(on.DebugTranscript);
    }

    [Fact]
    public void Load_lets_the_process_environment_override_the_env_file()
    {
        // v1's rule, carried over: a deployment states its posture as a unit /
        // container setting instead of editing the shared .env — the preview
        // unit turns the debug transcript on exactly this way, so a unit's
        // Environment= line must reach the options or it silently does nothing.
        string envPath = Path.Combine(Path.GetTempPath(), $"dotfit-agentic-test-{Guid.NewGuid():N}.env");
        File.WriteAllText(envPath, $"{AgenticServiceOptions.ApiKeyVar}=from-file\n");
        try
        {
            var runtime = new RuntimeOptions
            {
                AliasTablePath = "aliases.json",
                EnvFilePath = envPath,
                SupportContact = null,
            };

            var fromFile = AgenticServiceOptions.Load(runtime);
            Assert.False(fromFile.DebugTranscript);
            Assert.True(fromFile.IsAuthorized("Bearer from-file"));

            Environment.SetEnvironmentVariable(AgenticServiceOptions.DebugTranscriptVar, "1");
            Environment.SetEnvironmentVariable(AgenticServiceOptions.ApiKeyVar, "from-process");
            try
            {
                var overridden = AgenticServiceOptions.Load(runtime);
                Assert.True(overridden.DebugTranscript);
                Assert.True(overridden.IsAuthorized("Bearer from-process"));
                Assert.False(overridden.IsAuthorized("Bearer from-file"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgenticServiceOptions.DebugTranscriptVar, null);
                Environment.SetEnvironmentVariable(AgenticServiceOptions.ApiKeyVar, null);
            }
        }
        finally
        {
            File.Delete(envPath);
        }
    }

    private static AgenticServiceOptions Options() => AgenticServiceOptions.FromValues(
        Env((AgenticServiceOptions.ApiKeyVar, "k")), supportContact: null);

    [Fact]
    public void Every_rejection_happens_before_the_stream_opens()
    {
        AgenticServiceOptions options = Options();

        Assert.NotNull(options.Reject(null));
        Assert.NotNull(options.Reject(new AskBody { Question = "   " }));
        Assert.NotNull(options.Reject(new AskBody { Question = new string('x', 2_001) }));
        Assert.NotNull(options.Reject(new AskBody { Question = "q", Top = 0 }));
        Assert.NotNull(options.Reject(new AskBody { Question = "q", Top = 21 }));
        Assert.NotNull(options.Reject(new AskBody { Question = "q", RequestId = new string('x', 65) }));
        Assert.NotNull(options.Reject(new AskBody
        {
            Question = "q",
            History = [new AskBody.Turn { Role = "system", Text = "t" }],
        }));

        Assert.Null(options.Reject(new AskBody
        {
            Question = "q",
            Top = 8,
            History = [new AskBody.Turn { Role = "assistant", Text = "t" }],
        }));
    }
}

public class AskStreamTests
{
    private sealed class RecordingWriter : ISseWriter
    {
        public List<(string Event, string Json)> Frames { get; } = [];

        public Task WriteAsync(string eventName, object payload, CancellationToken ct)
        {
            Frames.Add((eventName, System.Text.Json.JsonSerializer.Serialize(payload, AskStream.Json)));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAssistant(params TurnEvent[] events) : IAgenticAssistant
    {
        public AskRequest? Seen { get; private set; }

        public async IAsyncEnumerable<TurnEvent> AskAsync(
            AskRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Seen = request;
            foreach (TurnEvent e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class RecordingTurnSink : ITurnSink
    {
        public List<TurnLog> Logs { get; } = [];
        public void Write(TurnLog log) => Logs.Add(log);
    }

    private static TurnResult Result(string answer = "Take 5 g daily [1].") => new()
    {
        AnswerText = answer,
        Sources =
        [
            new SourceRef
            {
                N = 1, Id = "pdsrg-a-001", SourceType = "pdsrg", Authority = 2,
                Title = "Dosing", Quotable = true, CitationUrl = "https://example.com/a",
            },
        ],
        ToolCalls = [],
        FirstDeltaMs = 900,
        TotalMs = 1500,
        BudgetExhausted = false,
        CitedSources = [1],
        Families = [],
    };

    private static AgenticServiceOptions Options() => AgenticServiceOptions.FromValues(
        new Dictionary<string, string> { [AgenticServiceOptions.ApiKeyVar] = "k" }, supportContact: null);

    private static async Task<(RecordingWriter Writer, RecordingTurnSink Sink)> RunAsync(
        IAgenticAssistant assistant, AskBody body)
    {
        var writer = new RecordingWriter();
        var sink = new RecordingTurnSink();
        await AskStream.RunAsync(
            assistant, body, writer, Options(), sink, NullTranscriptSink.Instance, CancellationToken.None);
        return (writer, sink);
    }

    [Fact]
    public async Task The_disclosure_is_sent_once_and_only_for_a_new_conversation()
    {
        var assistant = new FakeAssistant(new TurnResultEvent(Result()));

        (RecordingWriter first, _) = await RunAsync(assistant, new AskBody { Question = "hi" });
        Assert.Equal(AskStream.EventDisclosure, first.Frames[0].Event);

        (RecordingWriter later, _) = await RunAsync(
            assistant, new AskBody { Question = "hi", ConversationId = "abc" });
        Assert.DoesNotContain(later.Frames, f => f.Event == AskStream.EventDisclosure);
    }

    [Fact]
    public async Task There_is_no_retraction_event_on_this_branch()
    {
        // D3. The name is retired rather than reused — a caller that still
        // handles it will simply never see it.
        var assistant = new FakeAssistant(
            new TurnStageEvent(Stages.Search, "creatine"),
            new TurnSourceEvent(Result().Sources[0]),
            new TurnDeltaEvent("Take 5 g daily [1]."),
            new TurnResultEvent(Result()));

        (RecordingWriter writer, _) = await RunAsync(assistant, new AskBody { Question = "q" });

        Assert.DoesNotContain(writer.Frames, f => f.Event == "retraction");
        Assert.Equal(
            [AskStream.EventDisclosure, AskStream.EventStage, AskStream.EventSource,
             AskStream.EventDelta, AskStream.EventResult],
            writer.Frames.Select(f => f.Event));
    }

    [Fact]
    public async Task A_source_frame_carries_no_content()
    {
        // This endpoint is public; the source text is an operator diagnostic.
        var assistant = new FakeAssistant(new TurnSourceEvent(Result().Sources[0]), new TurnResultEvent(Result()));

        (RecordingWriter writer, _) = await RunAsync(assistant, new AskBody { Question = "q" });
        string frame = writer.Frames.Single(f => f.Event == AskStream.EventSource).Json;

        Assert.Contains("\"n\":1", frame, StringComparison.Ordinal);
        Assert.Contains("\"quotable\":true", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("content", frame, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_error_frame_keeps_the_kind_and_hides_the_message()
    {
        // The message can carry a deployment name or an Azure error body.
        var assistant = new FakeAssistant(
            new TurnErrorEvent("RequestFailedException", "deployment 'gpt-x' not found", "Sorry — try support."),
            new TurnDeltaEvent("Sorry — try support."),
            new TurnResultEvent(Result("Sorry — try support.")));

        (RecordingWriter writer, RecordingTurnSink sink) = await RunAsync(
            assistant, new AskBody { Question = "q", RequestId = "turn-1" });

        string frame = writer.Frames.Single(f => f.Event == AskStream.EventError).Json;
        Assert.Contains("RequestFailedException", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-x", frame, StringComparison.Ordinal);
        Assert.Equal(TurnLog.OutcomeError, sink.Logs.Single().Outcome);
    }

    [Fact]
    public async Task The_request_id_is_echoed_and_minted_when_absent()
    {
        var assistant = new FakeAssistant(new TurnResultEvent(Result()));

        (RecordingWriter mine, _) = await RunAsync(assistant, new AskBody { Question = "q", RequestId = "turn-7" });
        Assert.Contains("turn-7", mine.Frames.Single(f => f.Event == AskStream.EventResult).Json,
            StringComparison.Ordinal);

        (RecordingWriter minted, RecordingTurnSink sink) = await RunAsync(assistant, new AskBody { Question = "q" });
        Assert.Contains("request_id", minted.Frames.Single(f => f.Event == AskStream.EventResult).Json,
            StringComparison.Ordinal);
        Assert.NotNull(sink.Logs.Single().RequestId);
    }

    [Fact]
    public async Task History_and_top_are_passed_through_to_the_loop()
    {
        var assistant = new FakeAssistant(new TurnResultEvent(Result()));

        await RunAsync(assistant, new AskBody
        {
            Question = "and for someone smaller?",
            Top = 10,
            History = [new AskBody.Turn { Role = "user", Text = "how much creatine?" }],
        });

        Assert.Equal(10, assistant.Seen!.Top);
        Assert.Single(assistant.Seen.History);
    }

    [Fact]
    public async Task Every_turn_writes_exactly_one_log_line()
    {
        var assistant = new FakeAssistant(new TurnDeltaEvent("hi"), new TurnResultEvent(Result("hi")));

        (_, RecordingTurnSink sink) = await RunAsync(assistant, new AskBody { Question = "hi" });

        TurnLog log = Assert.Single(sink.Logs);
        Assert.Equal(TurnLog.OutcomeAnswered, log.Outcome);
        Assert.Equal(900, log.FirstDeltaMs);
    }
}
