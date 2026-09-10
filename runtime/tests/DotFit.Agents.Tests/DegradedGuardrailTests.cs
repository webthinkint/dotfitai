using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agents.Tests;

/// <summary>
/// The fail-open guardrail path (plan §12 coverage gap, progress open item 15).
///
/// The pre-check degrades to <c>escalate=false</c> on any API or parse failure
/// — deliberately, so an outage cannot take the assistant down. That means the
/// §12 escalation target rests on the other two defenses whenever it happens:
/// the answer agent's own hard-escalation instruction, and the post-check. The
/// eval harness cannot exercise this (it drives a live service that is not
/// failing on demand), so it is pinned here.
/// </summary>
public class DegradedGuardrailTests
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

    private static AIAgent AgentFor(params string[] replies) =>
        new ChatClientAgent(new ScriptedChatClient(replies), new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatOptions = new ChatOptions { Instructions = "test" },
        });

    /// <summary>A guardrail whose model call throws — the fail-open shape.</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new HttpRequestException("simulated outage");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new HttpRequestException("simulated outage");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AgentGuardrail DegradedGuardrail() =>
        new(new ChatClientAgent(new ThrowingChatClient(), new ChatClientAgentOptions
        {
            Name = "guardrail",
            ChatOptions = new ChatOptions { Instructions = "test" },
        }));

    private static KnowledgeAssistant Build(IGuardrail guardrail, FakeAnswerAgent answer) =>
        new(guardrail,
            new AgentQueryRewriter(
                AgentFor("""{"canonical_question":"q","product_mentions":[],"topics":[],"confidence":1}"""),
                ["Test Family"]),
            AliasTable.FromJson(AliasJson),
            new FakeKnowledgeSearch { Results = [TestDocs.Product(), TestDocs.Qa()] },
            answer, new SearchSettings());

    [Fact]
    public async Task PreCheckFailureDegradesInsteadOfBlocking()
    {
        GuardrailVerdict verdict = await DegradedGuardrail()
            .CheckAsync("I'm pregnant — is this safe?", ConversationHistory.Empty);

        Assert.True(verdict.Degraded);
        Assert.False(verdict.Escalate);      // fail-open by design
        Assert.Contains("degraded", verdict.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DegradedGuardrailStillReachesTheAnswerAgentsEscalationRule()
    {
        // With the pre-check down, the answer instructions are what stands
        // between the escalation list and an answered question — so they have
        // to actually be in the prompt the agent receives.
        var answer = new FakeAnswerAgent
        {
            Reply = "I can't help with that one — please contact the dotFIT support team.",
        };
        KnowledgeAssistant assistant = Build(DegradedGuardrail(), answer);

        AssistantResult result = await assistant.AskAsync("I'm pregnant — is this safe?");

        Assert.True(result.Guardrail.Degraded);
        Assert.False(result.Escalated);
        Assert.NotEmpty(answer.UserMessages);   // the model was asked, not short-circuited
        Assert.Contains("support team", result.DeliveredText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnswerInstructionsCarryTheWholeEscalationList()
    {
        // The post-check cannot recognise an escalation the guardrail missed,
        // so on the degraded path this text is the only thing that lists them.
        // The age trigger is off the list while the prompt wording is being
        // experimented on — AnswerInstructions says 16, GuardrailInstructions
        // escalates under_18, and which one is right is an owner ruling.
        string instructions = Prompts.AnswerInstructions;
        foreach (string trigger in new[]
                 {
                     "pregnancy", "managed medical condition", "eating-disorder",
                     "prescription medication", "calorie", "self-harm",
                 })
            Assert.Contains(trigger, instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARefusalWrittenByTheModelIsNotFailedForCitingNothing()
    {
        // The gap this pins: on the degraded path the guardrail says
        // escalate=false, so the post-check takes its non-escalation branch —
        // where an answer that cites none of the retrieved sources normally
        // fails. A correct model-written refusal cites nothing, and under
        // Gated a post-check failure withholds it, so a false failure here
        // would replace a good refusal with the handoff template (harmless)
        // or, on any caller that surfaces failures, look like a defect.
        var degraded = new GuardrailVerdict
        {
            Escalate = false, Degraded = true, Notes = "guardrail degraded: outage",
        };
        PostCheckResult result = PostChecker.Check(
            degraded,
            new RewriteResult { CanonicalQuestion = "q" },
            AliasExpansion.Empty,
            [TestDocs.Product(), TestDocs.Qa()],
            "I can't help with that one — please contact the dotFIT support team "
            + "or a healthcare professional.");

        Assert.True(result.Passed, string.Join(" | ", result.Failures));
    }
    [Fact]
    public void AnUncitedAnswerThatDoesNotRefuseStillFails()
    {
        // The refusal branch is entered only by an answer that both cites
        // nothing AND hands off. An ungrounded attempt at an answer must not
        // slip through it.
        PostCheckResult result = PostChecker.Check(
            new GuardrailVerdict { Escalate = false, Degraded = true },
            new RewriteResult { CanonicalQuestion = "q" },
            AliasExpansion.Empty,
            [TestDocs.Product(), TestDocs.Qa()],
            "Take two scoops before your workout.");

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("citation_presence"));
    }

    [Fact]
    public void ACitedAnswerMentioningAProfessionalIsNotMistakenForARefusal()
    {
        // "ask your healthcare professional" is ordinary advice inside a real,
        // cited answer — the citation marker is what tells them apart.
        PostCheckResult result = PostChecker.Check(
            new GuardrailVerdict { Escalate = false },
            new RewriteResult { CanonicalQuestion = "q" },
            AliasExpansion.Empty,
            [TestDocs.Product()],
            "The approved copy says it supports energy metabolism [1]. "
            + "If in doubt, ask a healthcare professional.");

        Assert.True(result.Passed, string.Join(" | ", result.Failures));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("refused and handed off"));
    }
}
