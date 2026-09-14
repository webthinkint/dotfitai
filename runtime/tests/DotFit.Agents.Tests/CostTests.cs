using System.Text.Json;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Cost;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Service;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agents.Tests;

/// <summary>
/// The pipeline's cost accounting (additive, owner-ruled 2026-09-15): every
/// model call and index query a turn makes lands in one
/// <see cref="TurnCost"/>, carried on <see cref="AssistantResult.Cost"/>, the
/// wire's <c>result</c> frame and the verdict log. Pinned here because the
/// whole point of the block is that the owners can stand behind the number —
/// a stage that stops reporting its usage must be noticed, not discovered at
/// invoice time.
/// </summary>
public class CostTests
{
    private const string AliasJson = """
        {
          "version": "test-1.0.0",
          "families": [{"family": "Test Family", "canonical_part_no": 9001, "part_nos": [9001], "n_variants": 1}],
          "deterministic_aliases": [],
          "llm_only_aliases": [],
          "context_only_tokens": {},
          "legacy_renames": [],
          "replacements": [],
          "discontinued": []
        }
        """;

    private const string Clear = """{"escalate":false,"reasons":[],"claim_trap":false,"notes":"none"}""";
    private const string SmallTalk =
        """{"escalate":false,"reasons":[],"claim_trap":false,"notes":"none","intent":"smalltalk"}""";
    private const string Escalate =
        """{"escalate":true,"reasons":["managed_condition"],"claim_trap":false,"notes":"diabetes"}""";
    private const string RewriteJson =
        """{"canonical_question":"canonical q","product_mentions":["Test Family"],"topics":[],"confidence":0.9}""";
    private const string ClaimsOk = """{"compliant":true,"violations":[],"evidence":[]}""";

    /// <summary>Round-number sheet, so the arithmetic is the assertion.</summary>
    private static PriceSheet Sheet() => new()
    {
        Id = "test-sheet",
        ChatInputPerMillion = 1m,
        ChatCachedInputPerMillion = 0.1m,
        ChatOutputPerMillion = 10m,
        SmallChatInputPerMillion = 2m,
        SmallChatCachedInputPerMillion = 0.2m,
        SmallChatOutputPerMillion = 20m,
        EmbeddingPerMillion = 0.2m,
        SearchPerThousand = 2m,
    };

    private static AIAgent AgentFor(string reply, UsageDetails? usage = null) =>
        new ChatClientAgent(new ScriptedChatClient(reply) { Usage = usage }, new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatOptions = new ChatOptions { Instructions = "test" },
        });

    private static KnowledgeAssistant Build(
        IGuardrail guardrail,
        IQueryRewriter rewriter,
        FakeKnowledgeSearch? search = null,
        FakeAnswerAgent? answer = null,
        IClaimsLanguageChecker? claims = null,
        IChatReplyAgent? chatReply = null,
        PriceSheet? prices = null) =>
        new(guardrail, rewriter, AliasTable.FromJson(AliasJson),
            search ?? new FakeKnowledgeSearch(), answer ?? new FakeAnswerAgent(),
            new SearchSettings(), claims, chatReply: chatReply, prices: prices);

    /// <summary>Every small-model stage reports this usage; the sheet prices it ×3 calls.</summary>
    private static UsageDetails SmallUsage() => new() { InputTokenCount = 100, OutputTokenCount = 10 };

    [Fact]
    public async Task ThePipelinePricesEveryStageIntoOneResultCost()
    {
        var search = new FakeKnowledgeSearch
        {
            Results = [TestDocs.Product()],
            EmbeddingTokens = 42,
        };
        var answer = new FakeAnswerAgent
        {
            Reply = "Test Product supports energy metabolism [1].",
            Usage = new UsageDetails { InputTokenCount = 500, CachedInputTokenCount = 200, OutputTokenCount = 50 },
        };
        var assistant = Build(
            new AgentGuardrail(AgentFor(Clear, SmallUsage())),
            new AgentQueryRewriter(AgentFor(RewriteJson, SmallUsage()), ["Test Family"]),
            search: search, answer: answer,
            claims: new AgentClaimsLanguageChecker(AgentFor(ClaimsOk, SmallUsage())),
            prices: Sheet());

        AssistantResult result = await assistant.AskAsync("what is Test Family for?");

        TurnCost cost = result.Cost!;
        // Three small-model calls (guardrail, rewrite, claims audit).
        Assert.Equal(300, cost.SmallChatInputTokens);
        Assert.Equal(30, cost.SmallChatOutputTokens);
        Assert.Equal(0.0006m + 0.0006m, cost.SmallChatUsd);
        // One main-deployment answer, cached subset kept apart.
        Assert.Equal(500, cost.ChatInputTokens);
        Assert.Equal(200, cost.ChatCachedInputTokens);
        Assert.Equal(50, cost.ChatOutputTokens);
        Assert.Equal(0.0003m + 0.00002m + 0.0005m, cost.ChatUsd);
        // One hybrid search: its embedding's own report, its served query.
        Assert.Equal(1, cost.EmbeddingCalls);
        Assert.Equal(42, cost.EmbeddingTokens);
        Assert.Equal(1, cost.IndexQueries);
        Assert.Equal(0, cost.RankerQueries);
        // The block is a function of one sheet, and it says which.
        Assert.Equal("test-sheet", cost.PriceSheet);
        Assert.Equal("USD", cost.Currency);
        Assert.Equal(cost.ChatUsd + cost.SmallChatUsd + cost.EmbeddingUsd + cost.SearchUsd, cost.TotalUsd);
    }

    [Fact]
    public async Task TheConversationalBranchPricesOnlyWhatRan()
    {
        // Guardrail + templated-branch reply, both small-model; no retrieval,
        // no answer agent, no index queries — the zeros are the assertion.
        var assistant = Build(
            new AgentGuardrail(AgentFor(SmallTalk, SmallUsage())),
            new AgentQueryRewriter(AgentFor(RewriteJson, SmallUsage()), ["Test Family"]),
            chatReply: new AgentChatReplyAgent(AgentFor("Hi! Ask me about dotFIT.", SmallUsage())),
            prices: Sheet());

        AssistantResult result = await assistant.AskAsync("hi there");

        TurnCost cost = result.Cost!;
        Assert.Equal(200, cost.SmallChatInputTokens);
        Assert.Equal(20, cost.SmallChatOutputTokens);
        Assert.Equal(0.0004m + 0.0004m, cost.SmallChatUsd);
        Assert.Equal(0, cost.ChatInputTokens);
        Assert.Equal(0m, cost.ChatUsd);
        Assert.Equal(0, cost.IndexQueries);
        Assert.Equal(0, cost.EmbeddingCalls);
        Assert.Equal(0m, cost.SearchUsd);
    }

    [Fact]
    public async Task TheEscalationPathPricesOnlyTheGuardrail()
    {
        // The refusal is deterministic (§11): one small-model call, nothing else.
        var search = new FakeKnowledgeSearch();
        var assistant = Build(
            new AgentGuardrail(AgentFor(Escalate, SmallUsage())),
            new AgentQueryRewriter(AgentFor(RewriteJson, SmallUsage()), ["Test Family"]),
            search: search,
            prices: Sheet());

        AssistantResult result = await assistant.AskAsync("I'm diabetic, what should I take?");

        Assert.True(result.Escalated);
        Assert.Null(search.Calls.FirstOrDefault());
        TurnCost cost = result.Cost!;
        Assert.Equal(100, cost.SmallChatInputTokens);
        Assert.Equal(10, cost.SmallChatOutputTokens);
        Assert.Equal(0.0002m + 0.0002m, cost.SmallChatUsd);
        Assert.Equal(0, cost.IndexQueries);
        Assert.Equal(0, cost.ChatInputTokens);
    }

    [Fact]
    public async Task A_missing_price_sheet_still_costs_at_the_builtin_sheet()
    {
        // Defaults keep the block honest about being provisional: the sheet id
        // says "builtin", and the counts are exact either way.
        var search = new FakeKnowledgeSearch { Results = [TestDocs.Product()], EmbeddingTokens = 7 };
        var assistant = Build(
            new AgentGuardrail(AgentFor(Clear)),
            new AgentQueryRewriter(AgentFor(RewriteJson), ["Test Family"]),
            search: search,
            answer: new FakeAnswerAgent { Reply = "Test Product supports energy metabolism [1]." },
            claims: new AgentClaimsLanguageChecker(AgentFor(ClaimsOk)));

        AssistantResult result = await assistant.AskAsync("what is Test Family for?");

        TurnCost cost = result.Cost!;
        Assert.Equal("builtin-2026-09", cost.PriceSheet);
        Assert.Equal(1, cost.IndexQueries);
        Assert.Equal(7, cost.EmbeddingTokens);
        Assert.Equal(0m, cost.SmallChatUsd);          // scripted stages reported no usage
        Assert.Equal(0m, cost.ChatUsd);
    }

    [Fact]
    public async Task The_wire_result_frame_carries_the_shared_cost_shape()
    {
        // One cost schema across both runtimes (TurnCost.ToWire): a caller
        // A/B-ing v1 against the agentic service reads one contract.
        var search = new FakeKnowledgeSearch { Results = [TestDocs.Product()], EmbeddingTokens = 42 };
        var assistant = Build(
            new AgentGuardrail(AgentFor(Clear, SmallUsage())),
            new AgentQueryRewriter(AgentFor(RewriteJson, SmallUsage()), ["Test Family"]),
            search: search,
            answer: new FakeAnswerAgent
            {
                Reply = "Test Product supports energy metabolism [1].",
                Usage = new UsageDetails { InputTokenCount = 500, CachedInputTokenCount = 200, OutputTokenCount = 50 },
            },
            claims: new AgentClaimsLanguageChecker(AgentFor(ClaimsOk, SmallUsage())),
            prices: Sheet());

        var writer = new RecordingSseWriter();
        await AskStream.RunAsync(
            assistant,
            new AskRequest { Question = "what is Test Family for?", RequestId = "turn-1" },
            writer, CancellationToken.None);

        string frame = writer.Frames.Single(f => f.Event == AskStream.EventResult).Json;
        Assert.Contains("\"price_sheet\":\"test-sheet\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"small_chat\":{\"input_tokens\":300", frame, StringComparison.Ordinal);
        Assert.Contains("\"cached_input_tokens\":200", frame, StringComparison.Ordinal);
        Assert.Contains("\"queries\":1", frame, StringComparison.Ordinal);
        Assert.Contains("\"total_usd\":", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_log_carries_the_cost_and_its_sheet()
    {
        AssistantResult result = BaseResult() with
        {
            Cost = new TurnCost
            {
                Currency = "USD",
                PriceSheet = "test-sheet",
                ChatInputTokens = 500,
                ChatCachedInputTokens = 200,
                ChatOutputTokens = 50,
                SmallChatInputTokens = 300,
                SmallChatCachedInputTokens = 0,
                SmallChatOutputTokens = 30,
                EmbeddingCalls = 1,
                EmbeddingTokens = 42,
                IndexQueries = 1,
                RankerQueries = 0,
                ChatUsd = 0.00082m,
                SmallChatUsd = 0.0012m,
                EmbeddingUsd = 0.0000084m,
                SearchUsd = 0.002m,
            },
        };

        VerdictLog log = VerdictLog.From(
            new AskRequest { Question = "q" }, "turn-1", DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(1500), result, null);
        string line = JsonSerializer.Serialize(log, AskStream.Json);

        Assert.Contains("\"cost\":{", line, StringComparison.Ordinal);
        Assert.Contains("\"price_sheet\":\"test-sheet\"", line, StringComparison.Ordinal);
        Assert.Contains("\"small_chat_input_tokens\":300", line, StringComparison.Ordinal);
        Assert.Equal("1.3.0", VerdictLog.SchemaVersion);
    }

    [Fact]
    public void A_run_that_never_finished_logs_no_cost()
    {
        // The error and abandon paths have no result and so no meter — the
        // field is absent rather than zero, because zero is a measurement and
        // there was none.
        VerdictLog log = VerdictLog.From(
            new AskRequest { Question = "q" }, "turn-1", DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(50), null, "Timeout");
        string line = JsonSerializer.Serialize(log, AskStream.Json);

        Assert.DoesNotContain("cost", line, StringComparison.Ordinal);
    }

    private static AssistantResult BaseResult() => new()
    {
        Question = "q",
        Guardrail = new GuardrailVerdict(),
        Rewrite = new RewriteResult { CanonicalQuestion = "q" },
        Expansion = AliasExpansion.Empty,
        Sources = [],
        AnswerText = "an answer",
        DeliveredText = "an answer",
        Citations = [],
        RenderedCitations = "",
        PostCheck = PostChecker.Check(new GuardrailVerdict(),
            new RewriteResult { CanonicalQuestion = "q" }, AliasExpansion.Empty, [], "an answer"),
        StageSeconds = new Dictionary<string, double>(),
    };

    /// <summary>Minimal SSE recorder, mirroring the one in <see cref="AskStreamTests"/>.</summary>
    private sealed class RecordingSseWriter : ISseWriter
    {
        public List<(string Event, string Json)> Frames { get; } = [];

        public Task WriteAsync(string eventName, object payload, CancellationToken ct)
        {
            Frames.Add((eventName, JsonSerializer.Serialize(payload, AskStream.Json)));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
