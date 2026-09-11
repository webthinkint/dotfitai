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
/// The per-request verdict log (plan §11, open item 20). These pin the two
/// things it is for: that a record exists on **every** terminal path, because
/// the item 21 ruling makes it the only reconstruction of what a preview
/// audience was shown; and that it carries **no question and no answer text**,
/// because the caller's database is the system of record and a second copy is
/// a new PII surface (§4 posture).
///
/// Fixtures are synthetic, and the "text" the leak tests look for is a marker
/// string — never real corpus text.
/// </summary>
public class VerdictLogTests
{
    /// <summary>The strings that stand in for customer words, none of which may be logged.</summary>
    private const string QuestionMarker = "QUESTIONTEXT";
    private const string AnswerMarker = "ANSWERTEXT";

    private sealed class RecordingSink : IVerdictSink
    {
        public List<VerdictLog> Entries { get; } = [];
        public Exception? Throw { get; init; }

        public void Write(VerdictLog entry)
        {
            if (Throw is not null)
                throw Throw;
            Entries.Add(entry);
        }

        public VerdictLog Single() => Assert.Single(Entries);
        public string Json() => JsonSerializer.Serialize(Single(), AskStream.Json);
    }

    /// <summary>Discards frames — these tests read the log, not the wire.</summary>
    private sealed class NullWriter : ISseWriter
    {
        public List<(string Event, string Json)> Frames { get; } = [];

        public Task WriteAsync(string eventName, object payload, CancellationToken ct)
        {
            Frames.Add((eventName, JsonSerializer.Serialize(payload, AskStream.Json)));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAssistant(params AssistantEvent[] events) : IKnowledgeAssistant
    {
        public Exception? Throw { get; init; }
        public TimeSpan? Delay { get; init; }

        public async IAsyncEnumerable<AssistantEvent> AskStreamAsync(
            string question, AskOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (Throw is not null)
                throw Throw;
            foreach (AssistantEvent e in events)
                yield return e;
            if (Delay is { } delay)
                await Task.Delay(delay, ct);
        }
    }

    private static AssistantResult Result(
        GuardrailVerdict? guardrail = null,
        PostCheckResult? postCheck = null,
        IReadOnlyList<RetrievedDocument>? sources = null,
        string answer = $"Grounded answer [1] about {AnswerMarker}.",
        bool withheld = false,
        AliasExpansion? expansion = null)
    {
        sources ??= [TestDocs.Product()];
        return new AssistantResult
        {
            Question = $"a question about {QuestionMarker}?",
            Guardrail = guardrail ?? new GuardrailVerdict(),
            Rewrite = new RewriteResult { CanonicalQuestion = $"canonical {QuestionMarker}" },
            Expansion = expansion ?? AliasExpansion.Empty,
            Sources = sources,
            AnswerText = answer,
            DeliveredText = withheld ? Prompts.WithheldMessage() : answer,
            Citations = CitationFormatter.Collect(answer, sources),
            RenderedCitations = "",
            PostCheck = postCheck ?? PostCheckResult.Pass([], null),
            StageSeconds = new Dictionary<string, double> { ["guardrail"] = 0.25, ["answer"] = 1.5 },
        };
    }

    private static async Task<RecordingSink> Run(
        FakeAssistant assistant, AskRequest? request = null,
        ServiceOptions? service = null, CancellationToken ct = default,
        RecordingSink? sink = null, NullWriter? writer = null)
    {
        sink ??= new RecordingSink();
        await AskStream.RunAsync(
            assistant, request ?? new AskRequest { Question = "q?" },
            writer ?? new NullWriter(), ct, service ?? new ServiceOptions { ApiKey = null }, sink);
        return sink;
    }

    // --- the PII promise -----------------------------------------------------

    [Fact]
    public async Task TheLogCarriesNeitherTheQuestionNorTheAnswer()
    {
        // The whole reason the log is safe to keep: the website DB is the system
        // of record for what was said, and this holds only what was decided.
        RecordingSink sink = await Run(new FakeAssistant(new ResultEvent(Result())));

        string json = sink.Json();
        Assert.DoesNotContain(QuestionMarker, json);
        Assert.DoesNotContain(AnswerMarker, json);
    }

    [Fact]
    public async Task FailureMessagesAreLoggedAsCheckNamesNotAsMessages()
    {
        // "claims_language: <the offending wording>" quotes the draft back. The
        // name is the datum open item 12 needs; the wording is the leak.
        var postCheck = new PostCheckResult(
            false,
            [$"claims_language: the draft said {AnswerMarker}", "citation_presence: the answer cites none"],
            [$"unknown_citations: 9 (not in the source list) {AnswerMarker}"],
            null);
        RecordingSink sink = await Run(new FakeAssistant(
            new ResultEvent(Result(postCheck: postCheck, withheld: true))));

        VerdictLog entry = sink.Single();
        Assert.Equal(["claims_language", "citation_presence"], entry.Failures);
        Assert.Equal(["unknown_citations"], entry.Warnings);
        Assert.DoesNotContain(AnswerMarker, sink.Json());
    }

    [Theory]
    [InlineData("claims_language: bad wording", "claims_language")]
    [InlineData("escalation_respected", "escalation_respected")]
    [InlineData("A sentence with no check name at all.", "unnamed")]
    [InlineData("Something: with a capitalised prefix", "unnamed")]
    [InlineData(": leading colon", "unnamed")]
    [InlineData("", "unnamed")]
    public void ACheckNameIsReadOrRefused(string message, string expected)
    {
        // Never guessed at: keeping the whole string when the prefix does not
        // parse is precisely the leak this method exists to prevent.
        Assert.Equal(expected, VerdictLog.CheckName(message));
    }

    [Fact]
    public async Task OffVocabularyEscalationReasonsCollapseToOther()
    {
        // Reasons come from a model. The structured schema constrains them to
        // its enum, but the degraded path does not — and an unrecognised "code"
        // is free text arriving in a log that promises to hold none. Notes,
        // which are free prose by design, are not logged at all.
        var guardrail = new GuardrailVerdict
        {
            Escalate = true,
            Reasons = ["under_18", $"the customer mentioned {QuestionMarker}"],
            Notes = $"model notes about {QuestionMarker}",
        };
        RecordingSink sink = await Run(new FakeAssistant(
            new ResultEvent(Result(guardrail: guardrail, answer: "Please contact the support team."))));

        Assert.Equal(["under_18", "other"], sink.Single().Reasons);
        Assert.DoesNotContain(QuestionMarker, sink.Json());
    }

    [Fact]
    public void OverlongIdsAreRejectedBeforeTheStreamOpens()
    {
        // conversation_id and request_id are the only caller-supplied values the
        // service retains, so they are the only smuggling channel into the log.
        var service = new ServiceOptions { ApiKey = null };

        Assert.Contains("conversation_id", service.Reject(new AskRequest
        {
            Question = "q?",
            ConversationId = new string('c', ServiceOptions.MaxIdChars + 1),
        })!);
        Assert.Contains("request_id", service.Reject(new AskRequest
        {
            Question = "q?",
            RequestId = new string('r', ServiceOptions.MaxIdChars + 1),
        })!);
        Assert.Null(service.Reject(new AskRequest
        {
            Question = "q?",
            ConversationId = Guid.NewGuid().ToString(),
            RequestId = Guid.NewGuid().ToString(),
        }));
    }

    // --- one record per request, on every terminal path -----------------------

    [Fact]
    public async Task AnAnsweredRequestRecordsItsVerdict()
    {
        var guardrail = new GuardrailVerdict { ClaimTrap = true };
        RecordingSink sink = await Run(
            new FakeAssistant(new ResultEvent(Result(guardrail: guardrail))),
            new AskRequest
            {
                Question = "q?",
                ConversationId = "conv-1",
                Top = 5,
                History = [new AskHistoryTurn { Role = "user", Text = "earlier" }],
            });

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeAnswered, entry.Outcome);
        Assert.Equal("conv-1", entry.ConversationId);
        Assert.Equal(VerdictLog.SchemaName, entry.Log);
        Assert.True(entry.PostCheckPassed);
        Assert.True(entry.ClaimTrap);
        Assert.False(entry.Escalated);
        Assert.False(entry.Withheld);
        Assert.Equal(5, entry.Top);
        Assert.Equal(1, entry.HistoryTurns);
        Assert.Equal(1500, entry.StageMs!["answer"]);
    }

    [Fact]
    public async Task AnEscalationRecordsWhetherItRestsOnAnEarlierTurn()
    {
        // Open item 19's flag is the one thing a complaint cannot be
        // reconstructed without: the customer sees the same handoff whether the
        // trigger was this turn or three turns back.
        var guardrail = new GuardrailVerdict
        {
            Escalate = true,
            Reasons = ["under_18"],
            HistoryTrigger = true,
        };
        RecordingSink sink = await Run(new FakeAssistant(
            new ResultEvent(Result(guardrail: guardrail, answer: "Please contact the support team."))));

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeEscalated, entry.Outcome);
        Assert.True(entry.Escalated);
        Assert.True(entry.HistoryTrigger);
        Assert.Equal(["under_18"], entry.Reasons);
    }

    [Fact]
    public async Task AConversationalTurnRecordsTheChitchatOutcome()
    {
        // Counted apart from `answered` on purpose (§11 intent branch): a run
        // of greetings would otherwise read as a healthy answer rate with a
        // citation rate of zero.
        var guardrail = new GuardrailVerdict { Intent = ConversationIntents.SmallTalk };
        RecordingSink sink = await Run(new FakeAssistant(
            new ResultEvent(Result(guardrail: guardrail, sources: [], answer: "Hi! Ask me anything."))));

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeChitchat, entry.Outcome);
        Assert.Equal(ConversationIntents.SmallTalk, entry.Intent);
        Assert.Equal(0, entry.NSources);
        Assert.Equal(0, entry.NCitations);
    }

    [Fact]
    public async Task AnOutOfScopeQuestionLogsItsIntentButIsStillAnswered()
    {
        // The only visibility on an intent that deliberately changes nothing:
        // this column is how the owner learns how much of the traffic is work
        // dotFIT support owns.
        var guardrail = new GuardrailVerdict { Intent = ConversationIntents.OutOfScope };
        RecordingSink sink = await Run(new FakeAssistant(new ResultEvent(Result(guardrail: guardrail))));

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeAnswered, entry.Outcome);
        Assert.Equal(ConversationIntents.OutOfScope, entry.Intent);
    }

    [Fact]
    public async Task AnOffVocabularyIntentIsLoggedAsQuestion()
    {
        // Same rule as the reason codes: the model's word never reaches the log
        // unfiltered, because a log that promises to hold no free text has to
        // enforce it on every field a model can write to.
        var guardrail = new GuardrailVerdict { Intent = $"smalltalk about {QuestionMarker}" };
        RecordingSink sink = await Run(new FakeAssistant(new ResultEvent(Result(guardrail: guardrail))));

        Assert.Equal(ConversationIntents.Question, sink.Single().Intent);
        Assert.DoesNotContain(QuestionMarker, sink.Json());
    }

    [Fact]
    public async Task AWithheldAnswerRecordsTheWithheldOutcome()
    {
        var postCheck = new PostCheckResult(
            false, ["claims_language: bad wording"], [],
            new ClaimsVerdict { Compliant = false, Violations = ["cure claim", "dosage claim"] });
        RecordingSink sink = await Run(new FakeAssistant(
            new ResultEvent(Result(postCheck: postCheck, withheld: true))));

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeWithheld, entry.Outcome);
        Assert.True(entry.Withheld);
        Assert.False(entry.PostCheckPassed);
        Assert.Equal(VerdictLog.ClaimsViolation, entry.Claims);
        Assert.Equal(2, entry.ClaimsViolations);
    }

    [Fact]
    public async Task APipelineFailureRecordsAnErrorKindAndNoMessage()
    {
        RecordingSink sink = await Run(new FakeAssistant
        {
            Throw = new InvalidOperationException("connection string 'secret' is bad"),
        });

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeError, entry.Outcome);
        Assert.Equal(nameof(InvalidOperationException), entry.ErrorKind);
        Assert.DoesNotContain("secret", sink.Json());
    }

    [Fact]
    public async Task ATimeoutRecordsItsOwnKind()
    {
        RecordingSink sink = await Run(
            new FakeAssistant(new StageEvent("guardrail", "clear")) { Delay = TimeSpan.FromSeconds(30) },
            service: new ServiceOptions { ApiKey = null, RequestTimeout = TimeSpan.FromMilliseconds(50) });

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeError, entry.Outcome);
        Assert.Equal("Timeout", entry.ErrorKind);
    }

    [Fact]
    public async Task AClientHangUpIsRecordedAsAbandonedRatherThanNotAtAll()
    {
        // The disconnect rethrows, so the log is written from a finally. A run
        // that produced no record is indistinguishable from one that never
        // happened — and "the customer never saw it" is itself the answer to a
        // complaint.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var sink = new RecordingSink();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(
            new FakeAssistant { Throw = new OperationCanceledException() },
            ct: aborted.Token, sink: sink));

        VerdictLog entry = sink.Single();
        Assert.Equal(VerdictLog.OutcomeAbandoned, entry.Outcome);
        Assert.Equal(VerdictLog.ClientClosedKind, entry.ErrorKind);
    }

    [Fact]
    public async Task ASinkThatThrowsDoesNotFailTheRequest()
    {
        // Logging is an operational concern; an answered request must not be
        // turned into a failure by it — and in the hang-up case a throw here
        // would swallow the cancellation in flight.
        var sink = new RecordingSink { Throw = new IOException("disk full") };
        var writer = new NullWriter();

        await AskStream.RunAsync(
            new FakeAssistant(new ResultEvent(Result())), new AskRequest { Question = "q?" },
            writer, CancellationToken.None, new ServiceOptions { ApiKey = null }, sink);

        Assert.Contains(AskStream.EventResult, writer.Frames.Select(f => f.Event));
    }

    // --- the join key --------------------------------------------------------

    [Fact]
    public async Task AMintedRequestIdIsEchoedOnTheResultFrame()
    {
        var writer = new NullWriter();
        RecordingSink sink = await Run(
            new FakeAssistant(new ResultEvent(Result())), writer: writer);

        string echoed = JsonDocument
            .Parse(writer.Frames.Single(f => f.Event == AskStream.EventResult).Json)
            .RootElement.GetProperty("request_id").GetString()!;
        Assert.NotEmpty(echoed);
        Assert.Equal(echoed, sink.Single().RequestId);
    }

    [Fact]
    public async Task ACallerSuppliedRequestIdIsKeptSoNoJoinIsNeeded()
    {
        RecordingSink sink = await Run(
            new FakeAssistant(new ResultEvent(Result())),
            new AskRequest { Question = "q?", RequestId = "turn-42" });

        Assert.Equal("turn-42", sink.Single().RequestId);
    }

    [Fact]
    public async Task AFailedTurnIsStillJoinable()
    {
        // The error frame carries the id too: the turns most likely to be
        // complained about are the ones that failed.
        var writer = new NullWriter();
        RecordingSink sink = await Run(
            new FakeAssistant { Throw = new InvalidOperationException("boom") }, writer: writer);

        string echoed = JsonDocument
            .Parse(writer.Frames.Single(f => f.Event == AskStream.EventError).Json)
            .RootElement.GetProperty("request_id").GetString()!;
        Assert.Equal(echoed, sink.Single().RequestId);
    }

    // --- the columns items 12 and 17 are computed from ------------------------

    [Fact]
    public async Task CitedAuthoritiesRecordWhetherApprovedCopyWasCited()
    {
        // Open item 17's citation rate is "did a product-claim answer cite
        // authority 1-2", which is answerable from this field and nothing else.
        IReadOnlyList<RetrievedDocument> sources =
            [TestDocs.Qa(), TestDocs.Product(), TestDocs.Pdsrg()];
        RecordingSink sink = await Run(new FakeAssistant(new ResultEvent(Result(
            sources: sources,
            answer: "Context [1] and approved copy [2].",
            expansion: new AliasExpansion([], ["Test Family"], [], [])))));

        VerdictLog entry = sink.Single();
        Assert.Equal(3, entry.NSources);
        Assert.Equal(2, entry.NCitations);
        Assert.Equal([1, 3], entry.CitedAuthorities);
        Assert.Equal(["Test Family"], entry.Families);
    }

    [Theory]
    [InlineData(null, false, false, true, VerdictLog.ClaimsNotRun)]
    [InlineData(true, true, false, true, VerdictLog.ClaimsSkipped)]
    [InlineData(true, false, true, true, VerdictLog.ClaimsDegraded)]
    [InlineData(true, false, false, true, VerdictLog.ClaimsCompliant)]
    [InlineData(true, false, false, false, VerdictLog.ClaimsViolation)]
    public async Task TheClaimsOutcomeTellsRanFromSkippedFromDegraded(
        bool? present, bool skipped, bool degraded, bool compliant, string expected)
    {
        // Open item 24's distinction, which open item 12's recall denominator
        // rests on: a draft the audit never looked at must not read as one it
        // cleared.
        ClaimsVerdict? claims = present is null
            ? null
            : new ClaimsVerdict { Skipped = skipped, Degraded = degraded, Compliant = compliant };
        var postCheck = compliant
            ? PostCheckResult.Pass([], claims)
            : new PostCheckResult(false, ["claims_language: bad wording"], [], claims);
        RecordingSink sink = await Run(new FakeAssistant(new ResultEvent(
            Result(postCheck: postCheck, withheld: !compliant))));

        Assert.Equal(expected, sink.Single().Claims);
    }

    // --- the wire format -----------------------------------------------------

    [Fact]
    public void TheSinkWritesOneSnakeCaseLinePerEntry()
    {
        // A collector reads these line by line out of stdout, where they share
        // the stream with the host's own logging — hence one line, and hence the
        // log/log_version discriminator.
        var output = new StringWriter();
        var sink = new JsonLinesVerdictSink(output);
        var entry = new VerdictLog
        {
            RequestId = "r1",
            Timestamp = DateTimeOffset.UnixEpoch,
            Outcome = VerdictLog.OutcomeAnswered,
            HistoryTrigger = true,
            NSources = 8,
        };

        sink.Write(entry);
        sink.Write(entry);

        string[] lines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        JsonElement parsed = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal(VerdictLog.SchemaName, parsed.GetProperty("log").GetString());
        Assert.Equal(VerdictLog.SchemaVersion, parsed.GetProperty("log_version").GetString());
        Assert.Equal("r1", parsed.GetProperty("request_id").GetString());
        Assert.True(parsed.GetProperty("history_trigger").GetBoolean());
        Assert.Equal(8, parsed.GetProperty("n_sources").GetInt32());
        // Absent rather than null: nothing was set, so nothing is claimed.
        Assert.False(parsed.TryGetProperty("conversation_id", out _));
        Assert.False(parsed.TryGetProperty("error_kind", out _));
    }

    [Fact]
    public async Task NoSinkMeansNoLoggingAndNoFailure()
    {
        // The CLI and the eval harness drive AskStream without one.
        var writer = new NullWriter();
        await AskStream.RunAsync(
            new FakeAssistant(new ResultEvent(Result())), new AskRequest { Question = "q?" },
            writer, CancellationToken.None);

        Assert.Contains(AskStream.EventResult, writer.Frames.Select(f => f.Event));
    }
}
