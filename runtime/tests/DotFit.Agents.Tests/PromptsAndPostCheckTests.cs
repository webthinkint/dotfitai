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
        Assert.Contains("[1] First Product — dotFIT approved product copy (authority 1)", message);
        Assert.Contains("url: https://example.com/products/test", message);
        Assert.Contains("[2] customer q — dotFIT customer Q&A (authority 3)", message);
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
    public void SourceLabelsCoverAllFiveTypes()
    {
        Assert.Contains("approved product copy", Prompts.SourceLabel("product", 1));
        Assert.Contains("Practitioner Dietary Supplement Reference Guide", Prompts.SourceLabel("pdsrg", 2));
        Assert.Contains("customer Q&A", Prompts.SourceLabel("qa", 3));
        Assert.Contains("podcast transcript", Prompts.SourceLabel("podcast", 4));
        Assert.Contains("menu description", Prompts.SourceLabel("menu_desc", 5));
        Assert.Contains("weird (authority 7)", Prompts.SourceLabel("weird", 7));
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
