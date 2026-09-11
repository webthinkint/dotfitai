using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agents.Tests;

public class KnowledgeAssistantTests
{
    private const string AliasJson = """
        {
          "version": "test-1.0.0",
          "families": [{"family": "Test Family", "canonical_part_no": 9001, "part_nos": [9001], "n_variants": 1}],
          "deterministic_aliases": [],
          "llm_only_aliases": [],
          "context_only_tokens": {},
          "legacy_renames": [{"deprecated": "OldTest", "current_family": "Test Family", "part_nos": [9001], "source": "f"}],
          "replacements": [],
          "discontinued": []
        }
        """;

    private const string Clear = """{"escalate":false,"reasons":[],"claim_trap":false,"notes":"none"}""";
    private const string Escalate = """{"escalate":true,"reasons":["managed_condition"],"claim_trap":false,"notes":"diabetes"}""";
    private const string Trap = """{"escalate":false,"reasons":[],"claim_trap":true,"notes":"disease claim"}""";

    private static KnowledgeAssistant Build(
        string guardrailReply = Clear,
        string? rewriteReply = null,
        FakeKnowledgeSearch? search = null,
        FakeAnswerAgent? answer = null,
        IClaimsLanguageChecker? claims = null,
        string? aliasJson = null)
    {
        var guardrail = new AgentGuardrail(AgentFor(guardrailReply));
        var rewriter = new AgentQueryRewriter(
            AgentFor(rewriteReply ?? """{"canonical_question":"q","product_mentions":[],"topics":[],"confidence":1}"""),
            ["Test Family"]);
        return new KnowledgeAssistant(
            guardrail, rewriter, AliasTable.FromJson(aliasJson ?? AliasJson),
            search ?? new FakeKnowledgeSearch(), answer ?? new FakeAnswerAgent(),
            new SearchSettings(), claims);
    }

    private static AIAgent AgentFor(string reply) =>
        new ChatClientAgent(new ScriptedChatClient(reply), new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatOptions = new ChatOptions { Instructions = "test" },
        });

    [Fact]
    public async Task HappyPathRunsEveryStageAndAssemblesTheResult()
    {
        var search = new FakeKnowledgeSearch
        {
            Results = [TestDocs.Product("Test Product"), TestDocs.Pdsrg(), TestDocs.Qa()],
        };
        var answer = new FakeAnswerAgent { Reply = "Test Product supports energy metabolism [1]. The guide agrees [2]." };
        var assistant = Build(
            rewriteReply: """{"canonical_question":"canonical q","product_mentions":["Test Family"],"topics":["t"],"confidence":0.9}""",
            search: search, answer: answer);

        var stages = new List<string>();
        AssistantResult? result = null;
        var deltas = new System.Text.StringBuilder();
        await foreach (var e in assistant.AskStreamAsync("what is Test Family for?"))
        {
            switch (e)
            {
                case StageEvent s: stages.Add(s.Stage); break;
                case DeltaEvent d: deltas.Append(d.Text); break;
                case ResultEvent r: result = r.Result; break;
            }
        }

        Assert.Equal(["guardrail", "rewrite", "aliases", "search", "answer", "post-check"], stages);
        Assert.NotNull(result);
        AssistantResult final = result!;
        Assert.Equal(answer.Reply, final.AnswerText);   // streamed deltas assemble to the same text
        Assert.Equal(deltas.ToString(), final.AnswerText);
        Assert.Equal(2, final.Citations.Count);
        Assert.Equal(3, final.Sources.Count);
        Assert.Equal(["Test Family"], final.Expansion.Families);
        Assert.True(final.PostCheck.Passed);
        Assert.True(result.StageSeconds.ContainsKey("answer"));
        // the search text carries the canonical question and the expanded family
        Assert.Contains("canonical q", search.Calls.Single().QueryText);
        Assert.Contains("Test Family", search.Calls.Single().QueryText);
        // the answer agent got the numbered sources and the question
        Assert.Contains("Customer question: what is Test Family for?", answer.UserMessages.Single());
        Assert.Contains("[1] Test Product — dotFIT approved product copy (authority 1)", answer.UserMessages.Single());
    }

    [Fact]
    public async Task EscalatedQuestionShortCircuitsBeforeRewriteSearchAndAnswer()
    {
        var search = new FakeKnowledgeSearch { Results = [TestDocs.Product()] };
        var answer = new FakeAnswerAgent { Reply = "should never run [1]" };
        var assistant = Build(
            guardrailReply: Escalate,
            rewriteReply: """{"canonical_question":"x","product_mentions":[],"topics":[],"confidence":1}""",
            search: search, answer: answer);

        AssistantResult result = await assistant.AskAsync("can I take this with my diabetes medication?");

        Assert.True(result.Escalated);
        Assert.Empty(answer.UserMessages);     // no LLM call on the escalation path
        Assert.Empty(search.Calls);
        Assert.Empty(result.Citations);
        Assert.Contains("dotFIT support team", result.AnswerText);
        Assert.Contains("a managed medical condition", result.AnswerText);
        Assert.True(result.PostCheck.Passed);  // the deterministic refusal verifies by construction
    }

    [Fact]
    public async Task NoSourcesProduceHonestNoInfoContext()
    {
        var answer = new FakeAnswerAgent { Reply = "I don't have sourced information on that — please contact dotFIT support." };
        var assistant = Build(
            rewriteReply: """{"canonical_question":"q","product_mentions":[],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch(), answer: answer);

        AssistantResult result = await assistant.AskAsync("something obscure");
        Assert.Contains("No sources were found", answer.UserMessages.Single());
        Assert.True(result.PostCheck.Passed); // no sources → citation rules are vacuous, not failed
    }

    [Fact]
    public async Task ClaimTrapAddsCorrectionNoteToTheAnswerContext()
    {
        var answer = new FakeAnswerAgent { Reply = "It does not treat any disease [1]." };
        var assistant = Build(
            guardrailReply: Trap,
            rewriteReply: """{"canonical_question":"q","product_mentions":["Test Family"],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch { Results = [TestDocs.Product()] }, answer: answer);

        await assistant.AskAsync("does Test Family cure diabetes?");
        Assert.Contains("presumes a claim", answer.UserMessages.Single());
    }

    [Fact]
    public async Task TheClaimsAuditSeesTheRenameNoteButNotTheClaimTrapNote()
    {
        // Open item 17, found in manual chat 2026-09-11: the §5 rename note
        // instructs the answer agent to mention the rename, and the audit never
        // saw it — so an obedient "OldTest is now Test Family" read as a product
        // claim no source supports, and the gated run retracted it.
        //
        // The claim-trap note is deliberately held back. It is guidance about
        // the question, not an attested fact, and telling the auditor a question
        // was a claim trap biases the verdict it exists to reach.
        var claimsClient = new ScriptedChatClient(
            """{"compliant":true,"violations":[],"evidence":[]}""");
        IClaimsLanguageChecker claims = new AgentClaimsLanguageChecker(
            new ChatClientAgent(claimsClient, new ChatClientAgentOptions { Name = "claims" }));

        var answer = new FakeAnswerAgent { Reply = "OldTest is now called Test Family [1]." };
        var assistant = Build(
            guardrailReply: Trap,
            rewriteReply: """{"canonical_question":"q","product_mentions":["OldTest"],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch { Results = [TestDocs.Product()] }, answer: answer, claims: claims);

        AssistantResult result = await assistant.AskAsync("tell me all about OldTest");

        Assert.True(result.PostCheck.Passed);
        string audited = claimsClient.Calls.Single().Messages.Last().Text ?? "";
        Assert.Contains("Established facts", audited);
        Assert.Contains("OldTest was renamed Test Family", audited);
        Assert.Contains("mention the rename", audited);
        Assert.DoesNotContain("presumes a claim", audited);
        // The answer agent got both, which is the divergence being closed.
        Assert.Contains("OldTest was renamed Test Family", answer.UserMessages.Single());
        Assert.Contains("presumes a claim", answer.UserMessages.Single());
    }

    [Fact]
    public async Task NonCompliantClaimsVerdictFailsThePostCheck()
    {
        var claimsClient = new ScriptedChatClient(
            """{"compliant":false,"violations":["cures diabetes"],"evidence":["none"]}""");
        IClaimsLanguageChecker claims = new AgentClaimsLanguageChecker(
            new ChatClientAgent(claimsClient, new ChatClientAgentOptions { Name = "claims" }));

        var answer = new FakeAnswerAgent { Reply = "It supports normal energy metabolism [1]." };
        var assistant = Build(
            rewriteReply: """{"canonical_question":"q","product_mentions":["Test Family"],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch { Results = [TestDocs.Product()] }, answer: answer, claims: claims);

        AssistantResult result = await assistant.AskAsync("what does Test Family do?");
        Assert.False(result.PostCheck.Passed);
        Assert.Contains(result.PostCheck.Failures, f => f.Contains("cures diabetes"));
    }

    [Fact]
    public async Task ClaimsCheckAuditsContextOnlySourceSets()
    {
        // Open item 24: a set with no approved copy is the high-risk case, not
        // an exempt one — an answer built entirely from Q&A and podcast context
        // is where an unsupported product claim is most likely. The audit runs,
        // and sees the context-only docs it must judge against.
        var claimsClient = new ScriptedChatClient(
            """{"compliant":false,"violations":["takes two capsules daily"],"evidence":["none"]}""");
        IClaimsLanguageChecker claims = new AgentClaimsLanguageChecker(
            new ChatClientAgent(claimsClient, new ChatClientAgentOptions { Name = "claims" }));

        ClaimsVerdict verdict = await claims.CheckAsync(
            "q", "answer [1].", [TestDocs.Qa(), TestDocs.Qa("another")], []);

        Assert.False(verdict.Compliant);
        Assert.False(verdict.Degraded);
        Assert.False(verdict.Skipped);
        string sent = claimsClient.Calls.Single().Messages.Last().Text ?? "";
        Assert.Contains("CONTEXT ONLY", sent);
        Assert.DoesNotContain("QUOTABLE FOR PRODUCT CLAIMS", sent);
        Assert.Contains("Expert answer from the QA corpus.", sent);
    }

    [Fact]
    public async Task ClaimsCheckIsSkippedWhenNothingWasRetrieved()
    {
        // Nothing retrieved: no draft grounding to audit and no list to resolve
        // its [n] against. It says it never looked, rather than reporting a
        // pass it did not make — the eval harness reads this to keep un-audited
        // drafts out of the recall denominator (open item 12).
        var claimsClient = new ScriptedChatClient(
            """{"compliant":false,"violations":["never reached"],"evidence":["none"]}""");
        IClaimsLanguageChecker claims = new AgentClaimsLanguageChecker(
            new ChatClientAgent(claimsClient, new ChatClientAgentOptions { Name = "claims" }));

        ClaimsVerdict verdict = await claims.CheckAsync("q", "no sourced information.", [], []);

        Assert.True(verdict.Compliant);
        Assert.False(verdict.Degraded);
        Assert.True(verdict.Skipped);
        Assert.Empty(claimsClient.Calls);

        ClaimsVerdict judged = await claims.CheckAsync(
            "q", "answer [2].", [TestDocs.Product(), TestDocs.Qa()], []);
        Assert.False(judged.Compliant);
        Assert.False(judged.Skipped);
        string sent = claimsClient.Calls.Single().Messages.Last().Text ?? "";
        Assert.Contains("CONTEXT ONLY", sent);
        Assert.Contains("Expert answer from the QA corpus.", sent);
    }

    [Fact]
    public async Task DegradedGuardrailStillAnswers()
    {
        var guardrail = new AgentGuardrail(AgentFor("garbage"));
        var rewriter = new AgentQueryRewriter(
            AgentFor("""{"canonical_question":"q","product_mentions":[],"topics":[],"confidence":1}"""), ["Test Family"]);
        var answer = new FakeAnswerAgent { Reply = "Sourced answer [1]." };
        var assistant = new KnowledgeAssistant(
            guardrail, rewriter, AliasTable.FromJson(AliasJson),
            new FakeKnowledgeSearch { Results = [TestDocs.Product()] }, answer, new SearchSettings());

        AssistantResult result = await assistant.AskAsync("anything");
        Assert.True(result.Guardrail.Degraded);
        Assert.Equal(answer.Reply, result.AnswerText);
    }

    /// <summary>A claims checker that always reports the answer non-compliant.</summary>
    private static IClaimsLanguageChecker NonCompliantClaims() =>
        new AgentClaimsLanguageChecker(new ChatClientAgent(
            new ScriptedChatClient("""{"compliant":false,"violations":["unapproved claim"],"evidence":["none"]}"""),
            new ChatClientAgentOptions { Name = "claims" }));

    [Fact]
    public async Task GatedModeHoldsEveryDeltaUntilThePostCheckHasRun()
    {
        var answer = new FakeAnswerAgent { Reply = "Test Product supports energy metabolism [1]." };
        var assistant = Build(
            rewriteReply: """{"canonical_question":"q","product_mentions":["Test Family"],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch { Results = [TestDocs.Product()] }, answer: answer);

        var order = new List<string>();
        var deltas = new System.Text.StringBuilder();
        AssistantResult? result = null;
        await foreach (var e in assistant.AskStreamAsync("what is Test Family for?",
            new AskOptions { StreamMode = AnswerStreamMode.Gated }))
        {
            switch (e)
            {
                case StageEvent s: order.Add($"stage:{s.Stage}"); break;
                case DeltaEvent d: order.Add("delta"); deltas.Append(d.Text); break;
                case RetractionEvent: order.Add("retraction"); break;
                case ResultEvent r: result = r.Result; break;
            }
        }

        // no delta may precede the post-check stage event
        Assert.True(order.IndexOf("delta") > order.IndexOf("stage:post-check"));
        Assert.DoesNotContain("retraction", order);
        Assert.NotNull(result);
        Assert.Equal(answer.Reply, deltas.ToString());
        Assert.Equal(answer.Reply, result!.DeliveredText);
        Assert.False(result.Withheld);
        Assert.True(result.PostCheck.Passed);
    }

    [Fact]
    public async Task GatedFailureWithholdsTheAnswerAndDeliversTheHandoff()
    {
        var answer = new FakeAnswerAgent { Reply = "Test Product cures everything [1]." };
        var assistant = Build(
            rewriteReply: """{"canonical_question":"q","product_mentions":["Test Family"],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch { Results = [TestDocs.Product()] },
            answer: answer, claims: NonCompliantClaims());

        var deltas = new System.Text.StringBuilder();
        RetractionEvent? retraction = null;
        AssistantResult? result = null;
        await foreach (var e in assistant.AskStreamAsync("what does Test Family do?",
            new AskOptions { StreamMode = AnswerStreamMode.Gated }))
        {
            switch (e)
            {
                case DeltaEvent d: deltas.Append(d.Text); break;
                case RetractionEvent x: retraction = x; break;
                case ResultEvent r: result = r.Result; break;
            }
        }

        Assert.NotNull(retraction);
        Assert.Equal(AnswerStreamMode.Gated, retraction!.Mode);
        Assert.Contains("unapproved claim", retraction.Reason);
        // the caller never saw the failing text; it survives for tracing and §12 eval
        Assert.DoesNotContain("cures everything", deltas.ToString());
        Assert.Equal(Prompts.WithheldMessage(), deltas.ToString());
        Assert.NotNull(result);
        Assert.Equal(answer.Reply, result!.AnswerText);
        Assert.Equal(Prompts.WithheldMessage(), result.DeliveredText);
        Assert.True(result.Withheld);
        Assert.Contains("dotFIT support team", result.DeliveredText);
        Assert.False(result.PostCheck.Passed);
    }

    [Fact]
    public async Task LiveFailureDeliversTheAnswerThenRetractsIt()
    {
        var answer = new FakeAnswerAgent { Reply = "Test Product cures everything [1]." };
        var assistant = Build(
            rewriteReply: """{"canonical_question":"q","product_mentions":["Test Family"],"topics":[],"confidence":1}""",
            search: new FakeKnowledgeSearch { Results = [TestDocs.Product()] },
            answer: answer, claims: NonCompliantClaims());

        var deltas = new System.Text.StringBuilder();
        RetractionEvent? retraction = null;
        AssistantResult? result = null;
        await foreach (var e in assistant.AskStreamAsync("what does Test Family do?"))  // Live is the default
        {
            switch (e)
            {
                case DeltaEvent d:
                    Assert.Null(retraction);   // deltas all arrive before the retraction
                    deltas.Append(d.Text);
                    break;
                case RetractionEvent x: retraction = x; break;
                case ResultEvent r: result = r.Result; break;
            }
        }

        Assert.NotNull(retraction);
        Assert.Equal(AnswerStreamMode.Live, retraction!.Mode);
        // live mode cannot unsay it: the text was delivered, the event only says so
        Assert.Equal(answer.Reply, deltas.ToString());
        Assert.Equal(answer.Reply, result!.DeliveredText);
        Assert.False(result.Withheld);
        Assert.False(result.PostCheck.Passed);
    }
}
