using DotFit.Agents.Retrieval;

namespace DotFit.Agents.Tests;

/// <summary>
/// The pure part of the search path. <c>AzureKnowledgeSearch</c> itself needs
/// Azure, but the filter it sends does not — and the filter is where a
/// caller-supplied <c>--filter</c> meets the is_current contract.
/// </summary>
public class SearchFilterTests
{
    private const string Current = "is_current eq true";

    [Fact]
    public void NoExtraFilterKeepsTheCurrencyContract()
    {
        Assert.Equal(Current, AzureKnowledgeSearch.BuildFilter(Current, null));
        Assert.Equal(Current, AzureKnowledgeSearch.BuildFilter(Current, "   "));
    }

    [Fact]
    public void ExtraFilterIsAndedAndParenthesized()
    {
        // The parentheses are the point: a top-level `or` would otherwise widen
        // the query past is_current and pull superseded documents back in.
        Assert.Equal(
            "is_current eq true and (authority eq 1 or authority eq 2)",
            AzureKnowledgeSearch.BuildFilter(Current, " authority eq 1 or authority eq 2 "));
    }

    [Fact]
    public void ExtraFilterSurvivesACustomCurrencyFilter()
    {
        Assert.Equal(
            "is_current eq true and source_type eq 'pdsrg' and (part_nos/any(p: p eq 1009))",
            AzureKnowledgeSearch.BuildFilter(
                "is_current eq true and source_type eq 'pdsrg'", "part_nos/any(p: p eq 1009)"));
    }
}
