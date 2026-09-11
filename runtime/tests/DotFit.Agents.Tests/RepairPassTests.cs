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
/// The §11 stage 6b repair pass: a claims-only post-check failure earns exactly
/// one bounded edit, judged again by the same checks.
///
/// The behavior it replaced is the thing these tests are really guarding.
/// "How much creatine should I take?" returned three bullets quoted verbatim
/// from the approved copy plus one appended sentence that carried a NO7
/// Preworkout3 statement onto CreatineMonohydrate; the audit flagged the
/// sentence, correctly, and the gate discarded all four. So the tests come in
/// pairs — what the pass now saves, and what it is still not allowed to do:
/// no second attempt, no repairing a shape failure, no losing the first
/// verdict, and nothing released that the checks have not passed.
/// </summary>
public class RepairPassTests
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
    private const string Rewrite =
        """{"canonical_question":"q","product_mentions":["Test Family"],"topics":[],"confidence":1}""";

    private const string Violation =
        """{"compliant":false,"violations":["loading is optional"],"evidence":["[2] the guide is about a different product"]}""";
    private const string Compliant = """{"compliant":true,"violations":[],"evidence":[]}""";

    private const string Draft = "Take one scoop daily [1]. Loading is optional [2].";
    private const string Repaired = "Take one scoop daily [1].";

    private static AIAgent AgentFor(params string[] replies) =>
        new ChatClientAgent(new ScriptedChatClient(replies), new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatOptions = new ChatOptions { Instructions = "test" },
        });

    /// <summary>The claims audit, scripted one verdict per call in order.</summary>
    private static IClaimsLanguageChecker Claims(params string[] verdicts) =>
        new AgentClaimsLanguageChecker(AgentFor(verdicts));

    private static KnowledgeAssistant Build(
        FakeAnswerAgent answer, IClaimsLanguageChecker? claims, FakeKnowledgeSearch? search = null) =>
        new(new AgentGuardrail(AgentFor(Clear)),
            new AgentQueryRewriter(AgentFor(Rewrite), ["Test Family"]),
            AliasTable.FromJson(AliasJson),
            search ?? new FakeKnowledgeSearch { Results = [TestDocs.Product(), TestDocs.Pdsrg()] },
            answer, new SearchSettings(), claims);

    private sealed record Run(
        List<string> Stages, List<string> Details, List<string> Order,
        string Deltas, List<string> Retractions, AssistantResult Result);

    private static async Task<Run> Drive(
        KnowledgeAssistant assistant, AnswerStreamMode mode = AnswerStreamMode.Gated, bool repair = true)
    {
        List<string> stages = [], details = [], order = [], retractions = [];
        var deltas = new System.Text.StringBuilder();
        AssistantResult? result = null;
        await foreach (AssistantEvent e in assistant.AskStreamAsync(
            "how much should I take?", new AskOptions { StreamMode = mode, Repair = repair }))
        {
            switch (e)
            {
                case StageEvent s:
                    stages.Add(s.Stage);
                    details.Add(s.Detail);
                    order.Add($"stage:{s.Stage}");
                    break;
                case DeltaEvent d:
                    deltas.Append(d.Text);
                    order.Add("delta");
                    break;
                case RetractionEvent x:
                    retractions.Add(x.Reason);
                    order.Add("retraction");
                    break;
                case ResultEvent r:
                    result = r.Result;
                    break;
            }
        }
        Assert.NotNull(result);
        return new Run(stages, details, order, deltas.ToString(), retractions, result);
    }

    [Fact]
    public async Task AClaimsOnlyFailureEarnsOneRepairPassAndTheRepairIsDelivered()
    {
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);
        answer.Replies.Enqueue(Repaired);

        Run run = await Drive(Build(answer, Claims(Violation, Compliant)));

        // The customer receives the repaired answer, and — under Gated, where
        // nothing had been shown — is told nothing about a draft that never
        // left the building.
        Assert.Equal(Repaired, run.Deltas);
        Assert.Empty(run.Retractions);
        Assert.False(run.Result.Withheld);
        Assert.True(run.Result.PostCheck.Passed);

        // Both drafts and both verdicts survive on the result.
        Assert.True(run.Result.Repaired);
        Assert.Equal(Draft, run.Result.PreRepairAnswerText);
        Assert.Equal(Repaired, run.Result.AnswerText);
        Assert.NotNull(run.Result.PreRepairPostCheck);
        Assert.False(run.Result.PreRepairPostCheck!.Passed);
        Assert.Contains(run.Result.PreRepairPostCheck.Failures,
            f => f.StartsWith("claims_language:", StringComparison.Ordinal));

        // The stage stream shows the second judgment, not just the second draft.
        Assert.Contains("repair", run.Stages);
        Assert.Equal(2, run.Stages.Count(s => s == "post-check"));
        Assert.Contains(run.Details, d => d == "PASS (repaired)");
        Assert.True(run.Result.StageSeconds.ContainsKey("repair"));
        Assert.True(run.Result.StageSeconds.ContainsKey("post-check-repair"));
    }

    [Fact]
    public async Task TheRepairTurnIsGroundedInTheSameSourcesAndNamesTheFlaggedWording()
    {
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);
        answer.Replies.Enqueue(Repaired);

        await Drive(Build(answer, Claims(Violation, Compliant)));

        Assert.Equal(2, answer.UserMessages.Count);
        string repairMessage = answer.UserMessages[1];

        // Same numbered sources as the draft was written from — anything else
        // and the [n] markers it is editing mean something different.
        Assert.StartsWith(answer.UserMessages[0], repairMessage, StringComparison.Ordinal);
        Assert.Contains(Draft, repairMessage, StringComparison.Ordinal);
        Assert.Contains("loading is optional", repairMessage, StringComparison.Ordinal);
        Assert.Contains("Change nothing else", repairMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepairedDraftThatFailsAgainIsWithheldAndGetsNoFurtherAttempt()
    {
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);
        answer.Replies.Enqueue("Still says loading is optional [2].");

        Run run = await Drive(Build(answer, Claims(Violation, Violation)));

        // Once. Not until it passes.
        Assert.Equal(2, answer.UserMessages.Count);
        Assert.Single(run.Retractions);
        Assert.True(run.Result.Withheld);
        Assert.Equal(Prompts.WithheldMessage(Prompts.DefaultSupportContact), run.Result.DeliveredText);
        Assert.Contains(run.Details, d => d.StartsWith("FAIL after repair", StringComparison.Ordinal));

        // Repaired, and still withheld: the row has to be readable as both.
        Assert.True(run.Result.Repaired);
        Assert.False(run.Result.PostCheck.Passed);
    }

    [Fact]
    public async Task NoRepairRestoresTheWholeOrNothingGate()
    {
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);

        Run run = await Drive(Build(answer, Claims(Violation)), repair: false);

        Assert.Single(answer.UserMessages);
        Assert.DoesNotContain("repair", run.Stages);
        Assert.False(run.Result.Repaired);
        Assert.Null(run.Result.PreRepairAnswerText);
        Assert.True(run.Result.Withheld);
    }

    [Fact]
    public async Task AFailureThatIsNotOnlyAboutClaimsIsNotRepaired()
    {
        // No [n] anywhere: citation_presence fails beside the claims audit, and
        // a draft that ignored the citation contract needs rewriting, not
        // editing. (No handoff phrasing either, so this is not read as the
        // answer agent's own refusal.)
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue("Take one scoop daily.");

        Run run = await Drive(Build(answer, Claims(Violation)));

        Assert.Single(answer.UserMessages);
        Assert.DoesNotContain("repair", run.Stages);
        Assert.False(run.Result.Repaired);
        Assert.True(run.Result.Withheld);
        Assert.Contains(run.Result.PostCheck.Failures,
            f => f.StartsWith("citation_presence:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveRetractsTheDraftItAlreadyShowedBeforeStreamingTheRepair()
    {
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);
        answer.Replies.Enqueue(Repaired);

        Run run = await Drive(Build(answer, Claims(Violation, Compliant)), AnswerStreamMode.Live);

        // Live has already rendered the draft, so it must be told to drop it —
        // and told before the replacement text arrives, not after.
        Assert.Single(run.Retractions);
        int retraction = run.Order.IndexOf("retraction");
        int repairStage = run.Order.IndexOf("stage:repair");
        Assert.True(retraction < repairStage);
        Assert.True(run.Order.LastIndexOf("delta") > retraction);

        Assert.Equal(Draft + Repaired, run.Deltas);   // both were streamed, in order
        Assert.Equal(Repaired, run.Result.AnswerText);
        Assert.Equal(Repaired, run.Result.DeliveredText);
        Assert.True(run.Result.PostCheck.Passed);
    }

    [Fact]
    public async Task TheRepairedDraftIsAuditedAgainRatherThanTrusted()
    {
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);
        answer.Replies.Enqueue(Repaired);

        var recording = new RecordingClaimsChecker([
            new ClaimsVerdict { Compliant = false, Violations = ["loading is optional"] },
            new ClaimsVerdict { Compliant = true },
        ]);
        Run run = await Drive(Build(answer, recording));

        Assert.Equal(2, recording.Answers.Count);
        Assert.Equal(Draft, recording.Answers[0]);
        Assert.Equal(Repaired, recording.Answers[1]);   // the repair does not report on itself
        Assert.True(run.Result.PostCheck.Passed);
    }

    [Fact]
    public async Task WithTheClaimsCheckOffThereIsNothingToRepair()
    {
        // The repair pass is driven by the audit's own violation quotes, so no
        // audit means no repair — and no claims failure to repair either.
        var answer = new FakeAnswerAgent();
        answer.Replies.Enqueue(Draft);

        var assistant = Build(answer, Claims(Violation));
        AssistantResult result = await assistant.AskAsync(
            "how much should I take?", new AskOptions { ClaimsCheck = false });

        Assert.Single(answer.UserMessages);
        Assert.False(result.Repaired);
        Assert.True(result.PostCheck.Passed);
    }

    // --- the predicate itself ---------------------------------------------------

    [Fact]
    public void RepairableClaimsOnlyHoldsForAClaimsFailureAndNothingElse()
    {
        var flagged = new ClaimsVerdict { Compliant = false, Violations = ["wording"] };

        Assert.True(new PostCheckResult(false, ["claims_language: wording"], [], flagged)
            .RepairableClaimsOnly);

        // Mixed is not repairable: All, not Any.
        Assert.False(new PostCheckResult(
            false, ["claims_language: wording", "citation_presence: none"], [], flagged)
            .RepairableClaimsOnly);

        // A shape failure on its own is not repairable.
        Assert.False(new PostCheckResult(false, ["citation_presence: none"], [], flagged)
            .RepairableClaimsOnly);

        // A passing check never is, whatever the audit said.
        Assert.False(PostCheckResult.Pass([], flagged).RepairableClaimsOnly);

        // Nor is a failure with no audit behind it, or one that named no wording
        // — there would be nothing to tell the repair turn to fix.
        Assert.False(new PostCheckResult(false, ["claims_language: wording"], [], null)
            .RepairableClaimsOnly);
        Assert.False(new PostCheckResult(false, ["claims_language: wording"], [],
            new ClaimsVerdict { Compliant = false }).RepairableClaimsOnly);
    }

    /// <summary>An audit that hands out scripted verdicts and records what it judged.</summary>
    private sealed class RecordingClaimsChecker(IReadOnlyList<ClaimsVerdict> verdicts)
        : IClaimsLanguageChecker
    {
        private int _call;
        public List<string> Answers { get; } = [];

        public Task<ClaimsVerdict> CheckAsync(
            string question, string answer, IReadOnlyList<RetrievedDocument> sources,
            IReadOnlyList<string> notes, CancellationToken ct = default)
        {
            Answers.Add(answer);
            return Task.FromResult(verdicts[Math.Min(_call++, verdicts.Count - 1)]);
        }
    }
}
