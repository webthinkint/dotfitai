using DotFit.Agents.Answering;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Aliases;

namespace DotFit.Agents.Tests;

public class PromptsTests
{
    [Fact]
    public void AnswerUserMessageNumbersSourcesWithLabelsAndLinks()
    {
        var sources = new List<Retrieval.RetrievedDocument>
        {
            TestDocs.Product("First Product"),
            TestDocs.Qa("customer q"),
        };
        string message = Prompts.BuildAnswerUserMessage("What is Test Product?", sources, ["note one"]);
        Assert.StartsWith("Customer question: What is Test Product?", message);
        Assert.Contains("[1] First Product — dotFIT approved product copy (authority 1) — QUOTABLE FOR PRODUCT CLAIMS", message);
        Assert.Contains("url: https://example.com/products/test", message);
        Assert.Contains("[2] customer q — dotFIT nutrition knowledge base (authority 3) — CONTEXT ONLY", message);
        Assert.Contains("dated: 2025-03-01", message);
        Assert.Contains("Notes:\n- note one", message);
    }

    [Fact]
    public void NoSourcesYieldsHonestNoInfoInstruction()
    {
        string message = Prompts.BuildAnswerUserMessage("q", [], []);
        Assert.Contains("No sources were found for this question.", message);
        Assert.Contains("suggest contacting dotFIT support", message);
    }

    [Fact]
    public void RefusalMessageHandsOffAndNamesReasons()
    {
        string refusal = Prompts.RefusalMessage(["pregnancy or breastfeeding"]);
        Assert.Contains("dotFIT support team", refusal);
        Assert.Contains("healthcare professional", refusal);
        Assert.Contains("pregnancy or breastfeeding", refusal);
        Assert.Contains("nutrition guidance, not medical advice", refusal);
    }

    [Fact]
    public void BothHandoffsCarryARealSupportRouteNotJustProse()
    {
        // Item 22: these two templates are the most-seen copy on the failure
        // paths, and a customer already being turned away must not also be sent
        // nowhere. The default route is corpus-attested — the PDSRG's own
        // "About dotFIT Worldwide" section publishes it — so it is approved copy
        // rather than a number we invented.
        foreach (string handoff in new[]
                 { Prompts.RefusalMessage(["pregnancy or breastfeeding"]), Prompts.WithheldMessage() })
        {
            Assert.Contains(Prompts.DefaultSupportContact, handoff);
            Assert.Contains("support@dotfit.com", handoff);
            // Still ends on the AI-identity line: the route is inserted before
            // it, not in place of it.
            Assert.EndsWith("(I'm an AI assistant — nutrition guidance, not medical advice.)", handoff);
        }
    }

    [Fact]
    public void AConfiguredRouteReplacesTheDefaultAndNoneDropsTheLine()
    {
        Assert.Contains("help@example.com", Prompts.WithheldMessage("help@example.com"));
        Assert.DoesNotContain("support@dotfit.com", Prompts.WithheldMessage("help@example.com"));

        // DOTFIT_SUPPORT_CONTACT=none leaves the wording exactly as it was
        // before item 22 — the prose already says to contact the support team.
        string bare = Prompts.WithheldMessage(supportContact: null);
        Assert.DoesNotContain("You can reach", bare);
        Assert.Contains("dotFIT support team", bare);
    }

    [Fact]
    public void TheSupportRouteDoesNotDisturbTheEscalationPostCheck()
    {
        // PostChecker reads the refusal for a handoff phrase; adding a line
        // after it must not turn a correct refusal into a failure.
        var escalated = new GuardrailVerdict { Escalate = true, Reasons = ["under-18"] };
        PostCheckResult result = PostChecker.Check(
            escalated, new RewriteResult { CanonicalQuestion = "q?" },
            AliasExpansion.Empty, [], Prompts.RefusalMessage(escalated.DisplayReasons));

        Assert.True(result.Passed);
    }

    [Fact]
    public void ClaimsMarkerSplitsApprovedCopyFromContextOnly()
    {
        // §3: products.json (authority 1) and the PDSRG (2) are the approved
        // claims corpus; customer Q&A (3) and podcasts (4) are context.
        Assert.True(Prompts.ClaimsQuotable(1));
        Assert.True(Prompts.ClaimsQuotable(2));
        Assert.False(Prompts.ClaimsQuotable(3));
        Assert.False(Prompts.ClaimsQuotable(4));
        Assert.Equal("QUOTABLE FOR PRODUCT CLAIMS", Prompts.ClaimsMarker(2));
        Assert.Equal("CONTEXT ONLY", Prompts.ClaimsMarker(3));
    }

    [Fact]
    public void EverySourceCarriesAClaimMarker()
    {
        // Regression: the 2026-09-08 smoke lifted claim wording from an
        // authority-3 Q&A and cited it beside an authority-2 chunk that never
        // said it. Prose in the instructions was not enough — the tag has to
        // ride on every rendered source.
        var sources = new List<Retrieval.RetrievedDocument>
        {
            TestDocs.Product("P"), TestDocs.Pdsrg("G"), TestDocs.Qa("Q"),
        };
        string message = Prompts.BuildAnswerUserMessage("q", sources, []);
        foreach (string line in message.Split((char)10).Where(l => l.StartsWith('[')))
            Assert.True(line.Contains("QUOTABLE FOR PRODUCT CLAIMS") || line.Contains("CONTEXT ONLY"),
                $"unmarked source line: {line}");
        Assert.Equal(2, message.Split("QUOTABLE FOR PRODUCT CLAIMS").Length - 1);
        Assert.Equal(1, message.Split("CONTEXT ONLY").Length - 1);
    }

    [Fact]
    public void AnswerInstructionsBindClaimsToTheQuotableTag()
    {
        Assert.Contains("QUOTABLE FOR PRODUCT CLAIMS", Prompts.AnswerInstructions);
        Assert.Contains("CONTEXT ONLY", Prompts.AnswerInstructions);
        // the fail-closed clause: a claim found only in context may not be stated as one
        Assert.Contains("appears ONLY in a CONTEXT ONLY", Prompts.AnswerInstructions);
        Assert.Contains("Never attach a QUOTABLE source's", Prompts.AnswerInstructions);
    }

    [Fact]
    public void ClaimsUserMessageNumbersEverySourceAsTheAnswerAgentSawThem()
    {
        // Regression, open items 17/12: the checker used to be handed the
        // authority 1-2 sources only, so it could not resolve a draft's [2] or
        // [3] and read anything grounded there as an invention. The dev sweep
        // withheld 51/125 answers on that, one of them a single sentence about
        // the carbohydrates in an apple, cited to the Q&A source it came from.
        var sources = new List<Retrieval.RetrievedDocument>
        {
            TestDocs.Product("P"), TestDocs.Qa("Q"), TestDocs.Pdsrg("G"),
        };
        string message = Prompts.BuildClaimsUserMessage("q", "answer [2].", sources);

        Assert.Contains("Draft answer:\nanswer [2].", message);
        Assert.Contains("[2] Q — dotFIT nutrition knowledge base (authority 3) — CONTEXT ONLY", message);
        Assert.Contains("Expert answer from the QA corpus.", message);
        // Numbering is the whole point: it must match the answer agent's list.
        foreach (int n in new[] { 1, 2, 3 })
        {
            var doc = sources[n - 1];
            string line = $"[{n}] {doc.Title} — {Prompts.SourceLabel(doc.SourceType, doc.Authority)}";
            Assert.Contains(line, message);
            Assert.Contains(line, Prompts.BuildAnswerUserMessage("q", sources, []));
        }
    }

    [Fact]
    public void ClaimsInstructionsScopeViolationsToProductClaims()
    {
        // The gate audits claim language, not general factual accuracy, and a
        // violation has to name the source number it was checked against — an
        // evidence-free flag against a source the checker never saw is
        // unfalsifiable, which is how item 12's denominator got its one entry.
        Assert.Contains("QUOTABLE FOR PRODUCT CLAIMS", Prompts.ClaimsInstructions);
        Assert.Contains("CONTEXT ONLY", Prompts.ClaimsInstructions);
        Assert.Contains("not factual accuracy in general", Prompts.ClaimsInstructions);
        Assert.Contains("is not a claim about a dotFIT product", Prompts.ClaimsInstructions);
        Assert.Contains("only if it is a product claim", Prompts.ClaimsInstructions);
        Assert.Contains("source number you checked it against", Prompts.ClaimsInstructions);
    }

    [Fact]
    public void SourceLabelsCoverAllFiveTypes()
    {
        Assert.Contains("approved product copy", Prompts.SourceLabel("product", 1));
        Assert.Contains("Practitioner Dietary Supplement Reference Guide", Prompts.SourceLabel("pdsrg", 2));
        Assert.Contains("nutrition knowledge base", Prompts.SourceLabel("qa", 3));
        Assert.Contains("expert discussion transcript", Prompts.SourceLabel("podcast", 4));
        Assert.Contains("menu description", Prompts.SourceLabel("menu_desc", 5));
        Assert.Contains("weird (authority 7)", Prompts.SourceLabel("weird", 7));
    }

    [Fact]
    public void SourceLabelsDescribeProvenanceNotThePipelineCorpus()
    {
        // The label is what the model echoes when it attributes an answer, so
        // it must not name the corpus the chunk was harvested from: the answer
        // agent was writing "dotFIT's customer Q&As typically recommend ...".
        // SourceKind, the customer-facing citation line, stays literal.
        foreach (string sourceType in new[] { "product", "pdsrg", "qa", "podcast", "menu_desc" })
        {
            Assert.DoesNotContain("Q&A", Prompts.SourceLabel(sourceType, 3));
            Assert.DoesNotContain("customer", Prompts.SourceLabel(sourceType, 3));
        }
        Assert.Equal("customer Q&A", Prompts.SourceKind("qa"));
    }
}

public class PostCheckerTests
{
    private static readonly GuardrailVerdict Clear = new() { Notes = "ok" };
    private static readonly RewriteResult NoMentions = new() { CanonicalQuestion = "q" };

    [Fact]
    public void AnswerWithoutCitationsFailsWhenSourcesExist()
    {
        var sources = new List<Retrieval.RetrievedDocument> { TestDocs.Product() };
        var result = PostChecker.Check(Clear, NoMentions, AliasExpansion.Empty, sources, "an answer with no markers");
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("citation_presence"));
    }

    [Fact]
    public void GroupedCitationsSatisfyTheCitationChecks()
    {
        // The whole answer is cited, just grouped — the post-check used to read
        // that as "cites none of the retrieved sources" and fail it.
        var sources = new List<Retrieval.RetrievedDocument> { TestDocs.Product(), TestDocs.Pdsrg() };
        var expansion = new AliasExpansion(["Test Family"], ["Test Family"], [9001], []);
        var result = PostChecker.Check(Clear, NoMentions, expansion, sources, "both sources agree [1, 2]");
        Assert.True(result.Passed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ProductClaimMustCiteApprovedCopy()
    {
        var sources = new List<Retrieval.RetrievedDocument> { TestDocs.Qa(), TestDocs.Product() };
        var expansion = new AliasExpansion(["Test Family"], ["Test Family"], [9001], []);
        var claimsButCitesOnlyQa = PostChecker.Check(Clear, NoMentions, expansion, sources, "claim [1]");
        Assert.False(claimsButCitesOnlyQa.Passed);
        Assert.Contains(claimsButCitesOnlyQa.Failures, f => f.StartsWith("product_claim_citation"));

        var claimsAndCitesProduct = PostChecker.Check(Clear, NoMentions, expansion, sources, "claim [2]");
        Assert.True(claimsAndCitesProduct.Passed);
    }

    [Fact]
    public void EscalatedRefusalMustHandOffAndNotCite()
    {
        var escalated = new GuardrailVerdict { Escalate = true, Reasons = ["managed_condition"] };
        string refusal = Prompts.RefusalMessage(escalated.DisplayReasons);
        var ok = PostChecker.Check(escalated, NoMentions, AliasExpansion.Empty, [], refusal);
        Assert.True(ok.Passed);

        var cites = PostChecker.Check(escalated, NoMentions, AliasExpansion.Empty, [TestDocs.Product()],
            refusal + " Meanwhile take [1] twice daily.");
        Assert.False(cites.Passed);
        Assert.Contains(cites.Failures, f => f.StartsWith("escalation_respected"));
    }

    [Fact]
    public void UnknownCitationsAreWarningsNotFailures()
    {
        var sources = new List<Retrieval.RetrievedDocument> { TestDocs.Product() };
        var result = PostChecker.Check(Clear, NoMentions, AliasExpansion.Empty, sources, "cite [1] and [7]");
        Assert.True(result.Passed);
        Assert.Contains(result.Warnings, w => w.StartsWith("unknown_citations"));
    }

    [Fact]
    public void NonCompliantClaimsVerdictFailsTheCheck()
    {
        var sources = new List<Retrieval.RetrievedDocument> { TestDocs.Product() };
        var claims = new ClaimsVerdict { Compliant = false, Violations = ["cures diabetes"] };
        var result = PostChecker.Check(Clear, NoMentions, AliasExpansion.Empty, sources, "answer [1]", claims);
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("claims_language") && f.Contains("cures diabetes"));
    }

    [Fact]
    public void DegradedClaimsCheckIsOnlyAWarning()
    {
        var sources = new List<Retrieval.RetrievedDocument> { TestDocs.Product() };
        var claims = new ClaimsVerdict { Compliant = true, Degraded = true };
        var result = PostChecker.Check(Clear, NoMentions, AliasExpansion.Empty, sources, "answer [1]", claims);
        Assert.True(result.Passed);
        Assert.Contains(result.Warnings, w => w.StartsWith("claims_language"));
    }
}
