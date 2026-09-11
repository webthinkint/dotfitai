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
/// The debug transcript log — the opt-in companion to the verdict log (owner
/// ruling 2026-09-11, recorded in <c>docs/decisions.md</c> Runtime §11).
/// These pin the three things it is for:
///
/// - that it carries **the words** — question, history, the withheld draft,
///   the delivered handoff and the full failure messages — which is the whole
///   reason it exists (retractions cannot be debugged from the verdict log);
/// - that it is **off unless asked for**, and that being on does not widen the
///   verdict log — <c>VerdictLog</c>'s guarantees hold regardless;
/// - that it is written on **every terminal path**, like the verdict, and
///   never fails a request for logging's sake.
///
/// Fixtures are synthetic, and the "text" here is a marker string — never real
/// corpus text.
/// </summary>
public class TranscriptLogTests
{
    /// <summary>Stands in for customer words; the transcript must carry it, the verdict must not.</summary>
    private const string QuestionMarker = "QUESTIONTEXT";
    private const string AnswerMarker = "ANSWERTEXT";
    private const string FailureMessage = $"claims_language: the draft asserts {AnswerMarker} as dotFIT's own";

    private sealed class RecordingSink : ITranscriptSink
    {
        public List<TranscriptLog> Entries { get; } = [];
        public Exception? Throw { get; init; }

        public void Write(TranscriptLog entry)
        {
            if (Throw is not null)
                throw Throw;
            Entries.Add(entry);
        }

        public TranscriptLog Single() => Assert.Single(Entries);
        public string Json() => JsonSerializer.Serialize(Single(), AskStream.Json);
    }

    private sealed class RecordingVerdictSink : IVerdictSink
    {
        public List<VerdictLog> Entries { get; } = [];
        public void Write(VerdictLog entry) => Entries.Add(entry);
        public string Json() => JsonSerializer.Serialize(Assert.Single(Entries), AskStream.Json);
    }

    private sealed class NullWriter : ISseWriter
    {
        public Task WriteAsync(string eventName, object payload, CancellationToken ct) =>
            Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAssistant(params AssistantEvent[] events) : IKnowledgeAssistant
    {
        public Exception? Throw { get; init; }

        public async IAsyncEnumerable<AssistantEvent> AskStreamAsync(
            string question, AskOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (Throw is not null)
                throw Throw;
            foreach (AssistantEvent e in events)
                yield return e;
        }
    }

    private static AssistantResult Result(
        GuardrailVerdict? guardrail = null,
        PostCheckResult? postCheck = null,
        bool withheld = false,
        string? notes = null)
    {
        string answer = $"Grounded answer [1] about {AnswerMarker}.";
        return new AssistantResult
        {
            Question = $"a question about {QuestionMarker}?",
            Guardrail = guardrail ?? new GuardrailVerdict { Notes = notes ?? "" },
            Rewrite = new RewriteResult { CanonicalQuestion = $"canonical {QuestionMarker}" },
            Expansion = AliasExpansion.Empty,
            Sources = [TestDocs.Product()],
            AnswerText = answer,
            DeliveredText = withheld ? Prompts.WithheldMessage() : answer,
            Citations = CitationFormatter.Collect(answer, [TestDocs.Product()]),
            RenderedCitations = "",
            PostCheck = postCheck ?? PostCheckResult.Pass([], null),
            StageSeconds = new Dictionary<string, double> { ["guardrail"] = 0.25, ["answer"] = 1.5 },
        };
    }

    private static async Task<RecordingSink> Run(
        FakeAssistant assistant, AskRequest? request = null,
        CancellationToken ct = default,
        RecordingSink? sink = null)
    {
        sink ??= new RecordingSink();
        await AskStream.RunAsync(
            assistant, request ?? new AskRequest { Question = $"about {QuestionMarker}?" },
            new NullWriter(), ct, new ServiceOptions { ApiKey = null }, null, sink);
        return sink;
    }

    // --- the words: the reason the record exists ---------------------------

    [Fact]
    public async Task AWithheldAnswerCarriesTheDraftTheHandoffAndTheFailureMessages()
    {
        // A retraction is exactly the thing the verdict log cannot explain: it
        // keeps the check's *name* and drops the message, which is the wording
        // the check rejected. The transcript keeps both texts and the message.
        var check = new PostCheckResult(false, [FailureMessage], [], null);
        var sink = await Run(new FakeAssistant(
            new RetractionEvent(FailureMessage, AnswerStreamMode.Gated),
            new ResultEvent(Result(postCheck: check, withheld: true))));

        TranscriptLog entry = sink.Single();
        Assert.Contains(QuestionMarker, entry.Question);
        Assert.Contains(AnswerMarker, entry.AnswerText);
        Assert.Contains("support", entry.DeliveredText);           // the handoff
        Assert.True(entry.Withheld);
        Assert.Equal(FailureMessage, Assert.Single(entry.Failures));
        // The retraction frame's reason, verbatim.
        Assert.Equal(FailureMessage, entry.Retraction);
    }

    [Fact]
    public async Task TheHistoryIsLoggedExactlyAsReceived()
    {
        var sink = await Run(new FakeAssistant(new ResultEvent(Result())),
            new AskRequest
            {
                Question = "q?",
                History =
                [
                    new AskHistoryTurn { Role = "user", Text = $"earlier {QuestionMarker}" },
                    new AskHistoryTurn { Role = "assistant", Text = "a reply" },
                ],
            });

        TranscriptLog entry = sink.Single();
        Assert.Equal(2, entry.History.Count);
        Assert.Equal("user", entry.History[0].Role);
        Assert.Equal($"earlier {QuestionMarker}", entry.History[0].Text);
        Assert.Equal("assistant", entry.History[1].Role);
        Assert.Equal("a reply", entry.History[1].Text);
    }

    [Fact]
    public async Task TheGuardrailNotesAreLoggedVerbatim()
    {
        // The free-prose notes are the one field the verdict log refuses on
        // principle (off-vocabulary model text); here they are the point.
        var sink = await Run(new FakeAssistant(new ResultEvent(
            Result(guardrail: new GuardrailVerdict { Notes = $"notes about {QuestionMarker}" }))));

        Assert.Equal($"notes about {QuestionMarker}", sink.Single().Notes);
    }

    [Fact]
    public async Task AnEscalationLogsTheRawReasonsAndTheRefusal()
    {
        var sink = await Run(new FakeAssistant(new ResultEvent(Result(
            guardrail: new GuardrailVerdict { Escalate = true, Reasons = ["under_18", "ad_hoc_code"] }))));

        TranscriptLog entry = sink.Single();
        Assert.True(entry.Escalated);
        // Raw, not folded to the display vocabulary.
        Assert.Equal(["under_18", "ad_hoc_code"], entry.Reasons);
        Assert.NotNull(entry.DeliveredText);
    }

    // --- every terminal path, like the verdict ------------------------------

    [Fact]
    public async Task APipelineFailureStillLogsTheQuestionAndTheKindOnly()
    {
        // The question plus the error kind is the debuggable part of a failed
        // request; the result fields are absent, not defaulted. And the kind
        // is still a type name — the message may quote config or stack.
        var sink = await Run(new FakeAssistant
        {
            Throw = new InvalidOperationException("connection string 'secret' is bad"),
        });

        TranscriptLog entry = sink.Single();
        Assert.Contains(QuestionMarker, entry.Question);
        Assert.Equal(nameof(InvalidOperationException), entry.ErrorKind);
        Assert.DoesNotContain("secret", sink.Json());
        Assert.Null(entry.AnswerText);
    }

    [Fact]
    public async Task AClientHangUpIsRecordedRatherThanLost()
    {
        // The disconnect rethrows, so the transcript is written from a finally,
        // like the verdict: an abandoned run still shows what was asked.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var sink = new RecordingSink();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(
            new FakeAssistant { Throw = new OperationCanceledException() },
            ct: aborted.Token, sink: sink));

        TranscriptLog entry = sink.Single();
        Assert.Contains(QuestionMarker, entry.Question);
        Assert.Equal(VerdictLog.ClientClosedKind, entry.ErrorKind);
        Assert.Null(entry.AnswerText);
    }

    // --- never the request's problem -----------------------------------------

    [Fact]
    public async Task ASinkThatThrowsDoesNotFailTheRequest()
    {
        var sink = new RecordingSink { Throw = new IOException("disk full") };
        await Run(new FakeAssistant(new ResultEvent(Result())), sink: sink);
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task NoSinkMeansNoTranscriptAndNoFailure()
    {
        // The CLI and the eval harness drive AskStream without a sink — that
        // must stay a no-op, not an exception path.
        await AskStream.RunAsync(
            new FakeAssistant(new ResultEvent(Result())),
            new AskRequest { Question = "q?" },
            new NullWriter(), default, new ServiceOptions { ApiKey = null }, null, null);
    }

    // --- the separation: on does not widen the verdict log -------------------

    [Fact]
    public async Task TurningTheTranscriptOnDoesNotWidenTheVerdictLog()
    {
        // The ruling's price is paid by this record, not by eroding that one:
        // with both sinks wired, the verdict still holds no question, no
        // answer and no failure message.
        var verdicts = new RecordingVerdictSink();
        var transcripts = new RecordingSink();
        var check = new PostCheckResult(false, [FailureMessage], [], null);
        await AskStream.RunAsync(
            new FakeAssistant(new ResultEvent(Result(postCheck: check, withheld: true))),
            new AskRequest { Question = $"about {QuestionMarker}?" },
            new NullWriter(), default, new ServiceOptions { ApiKey = null },
            verdicts, transcripts);

        string verdictJson = verdicts.Json();
        Assert.DoesNotContain(QuestionMarker, verdictJson);
        Assert.DoesNotContain(AnswerMarker, verdictJson);
        Assert.DoesNotContain("claims_language:", verdictJson);   // the name, not the message
        Assert.Contains(AnswerMarker, transcripts.Json());
    }

    // --- the wire shape ---------------------------------------------------------

    [Fact]
    public void TheSinkWritesOneSnakeCaseLinePerEntry()
    {
        // The tag and version let a collector pick these out of the host's own
        // console logging — same contract as the verdict lines.
        var sw = new StringWriter();
        var sink = new JsonLinesTranscriptSink(sw);
        sink.Write(TranscriptLog.From(
            new AskRequest { Question = "q?" }, "req-1",
            DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(5),
            result: null, retraction: null, errorKind: null));

        string line = Assert.Single(sw.ToString().TrimEnd().Split('\n'));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(TranscriptLog.SchemaName, doc.RootElement.GetProperty("log").GetString());
        Assert.Equal(TranscriptLog.SchemaVersion, doc.RootElement.GetProperty("log_version").GetString());
        Assert.Equal("req-1", doc.RootElement.GetProperty("request_id").GetString());
    }
}
