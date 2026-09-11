using System.Text.Json;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Cli;
using DotFit.Agents.Guardrails;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agents.Tests;

/// <summary>
/// The <c>ask --json</c> contract the §12 eval harness parses. These assert the
/// shape by name, not by round-tripping the library types: a field the harness
/// keys on must not vanish silently when a record changes.
/// </summary>
public class AskJsonTests
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

    private static AIAgent AgentFor(string reply) =>
        new ChatClientAgent(new ScriptedChatClient(reply), new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatOptions = new ChatOptions { Instructions = "test" },
        });

    private static KnowledgeAssistant Build(
        string guardrailReply, FakeKnowledgeSearch search, FakeAnswerAgent answer) =>
        new(new AgentGuardrail(AgentFor(guardrailReply)),
            new AgentQueryRewriter(
                AgentFor("""{"canonical_question":"canonical q","product_mentions":["Test Family"],"topics":["energy"],"confidence":0.9}"""),
                ["Test Family"]),
            AliasTable.FromJson(AliasJson), search, answer, new SearchSettings(), claimsChecker: null);

    private static async Task<JsonElement> AskJsonFor(
        string guardrailReply = Clear,
        string reply = "Test Product supports energy metabolism [1].",
        AnswerStreamMode mode = AnswerStreamMode.Live)
    {
        var search = new FakeKnowledgeSearch
        {
            Results = [TestDocs.Product("Test Product"), TestDocs.Pdsrg(), TestDocs.Qa()],
        };
        KnowledgeAssistant assistant = Build(guardrailReply, search, new FakeAnswerAgent { Reply = reply });
        AssistantResult result = await assistant.AskAsync("what is Test Family for?");
        return JsonDocument.Parse(AskJson.Serialize(result, mode)).RootElement;
    }

    [Fact]
    public async Task EmitsTheTopLevelHarnessContract()
    {
        JsonElement json = await AskJsonFor();

        foreach (string field in new[]
                 {
                     "question", "stream_mode", "escalated", "withheld", "guardrail", "rewrite",
                     "expansion", "sources", "answer_text", "delivered_text", "citations",
                     "rendered_citations", "post_check", "stage_seconds",
                 })
            Assert.True(json.TryGetProperty(field, out _), $"missing contract field: {field}");

        Assert.Equal("what is Test Family for?", json.GetProperty("question").GetString());
        Assert.Equal("Live", json.GetProperty("stream_mode").GetString());
        Assert.False(json.GetProperty("escalated").GetBoolean());
        Assert.False(json.GetProperty("withheld").GetBoolean());
    }

    [Fact]
    public async Task NamesAreSnakeCaseLikeThePipelineArtifacts()
    {
        JsonElement json = await AskJsonFor();

        Assert.True(json.GetProperty("guardrail").TryGetProperty("claim_trap", out _));
        // open item 19: the harness reads this to tell a conversation-level
        // catch from a single-turn one.
        Assert.True(json.GetProperty("guardrail").TryGetProperty("history_trigger", out _));
        Assert.True(json.GetProperty("rewrite").TryGetProperty("canonical_question", out _));
        Assert.True(json.GetProperty("expansion").TryGetProperty("part_nos", out _));
        JsonElement source = json.GetProperty("sources")[0];
        foreach (string field in new[] { "source_type", "citation_url", "is_current", "boosted_score", "reranker_score" })
            Assert.True(source.TryGetProperty(field, out _), $"missing source field: {field}");
    }

    [Fact]
    public async Task SourcesCarryContentAndRankForTheRagasMetrics()
    {
        JsonElement json = await AskJsonFor();
        JsonElement sources = json.GetProperty("sources");

        Assert.Equal(3, sources.GetArrayLength());
        Assert.Equal(1, sources[0].GetProperty("rank").GetInt32());
        Assert.Equal(3, sources[2].GetProperty("rank").GetInt32());
        // Faithfulness and context precision score the answer against the
        // retrieved context, so the context has to cross the process boundary.
        Assert.False(string.IsNullOrWhiteSpace(sources[0].GetProperty("content").GetString()));
        // Source-recall keys on the index id (golden sample.jsonl → qa-<id>).
        Assert.False(string.IsNullOrWhiteSpace(sources[0].GetProperty("id").GetString()));
    }

    [Fact]
    public async Task NullClaimsVerdictSerializesAsNullRatherThanVanishing()
    {
        JsonElement json = await AskJsonFor();  // built with claims: null
        JsonElement postCheck = json.GetProperty("post_check");

        Assert.True(postCheck.TryGetProperty("claims", out JsonElement claims));
        Assert.Equal(JsonValueKind.Null, claims.ValueKind);
        Assert.True(postCheck.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task WithheldAnswersKeepTheDraftForScoring()
    {
        // An escalation delivers the refusal template; §12 still needs to see
        // what the model would have said.
        JsonElement json = await AskJsonFor(
            guardrailReply: """{"escalate":true,"reasons":["managed_condition"],"claim_trap":false,"history_trigger":false,"notes":"diabetes"}""",
            mode: AnswerStreamMode.Gated);

        Assert.Equal("Gated", json.GetProperty("stream_mode").GetString());
        Assert.True(json.GetProperty("escalated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("delivered_text").GetString()));
    }

    [Fact]
    public async Task TheRepairFieldsAreAdditiveAndSayWhenNoRepairRan()
    {
        // §11 stage 6b. `answer_text` stays the draft that was judged and
        // delivered, so every metric the harness already computes goes on
        // scoring the text the customer got; these three are how a repaired run
        // becomes visible to one that looks. Present and false/null rather than
        // absent — the contract is asserted by name (open item 12 reads them).
        JsonElement json = await AskJsonFor();

        Assert.False(json.GetProperty("repaired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pre_repair_answer_text").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pre_repair_post_check").ValueKind);
    }
}
