using DotFit.Agents.Answering;
using DotFit.Agents.Retrieval;

namespace DotFit.Agents.Tests;

public class AuthorityBoostTests
{
    private static RetrievedDocument Doc(string id, int authority, double score) => new()
    {
        Id = id, SourceType = "qa", Authority = authority, Title = id, Content = "x",
        IsCurrent = true, Score = score,
    };

    [Fact]
    public void WeightsReorderNearTies()
    {
        var docs = new List<RetrievedDocument>
        {
            Doc("menu", 5, 0.0200),   // highest raw score, weakest tier
            Doc("product", 1, 0.0190),
        };
        var reranked = AuthorityBoost.Rerank(docs, AuthorityBoost.DefaultWeights, 2);
        Assert.Equal("product", reranked[0].Id); // 0.019 * 1.10 > 0.020 * 0.90
        Assert.Equal("menu", reranked[1].Id);
        Assert.True(reranked[0].BoostedScore > reranked[0].Score);
    }

    [Fact]
    public void AllOnesPreservesOrderAndDisables()
    {
        var docs = new List<RetrievedDocument> { Doc("a", 1, 0.01), Doc("b", 2, 0.02) };
        var reranked = AuthorityBoost.Rerank(docs, [0, 1, 1, 1, 1, 1], 2);
        Assert.Equal(["b", "a"], reranked.Select(d => d.Id));
        Assert.All(reranked, d => Assert.Equal(d.Score, d.BoostedScore));
    }

    [Fact]
    public void TruncatesToTopWithStableTiebreak()
    {
        var docs = new List<RetrievedDocument>
        {
            Doc("b", 1, 0.01), Doc("a", 1, 0.01), Doc("c", 1, 0.02),
        };
        var reranked = AuthorityBoost.Rerank(docs, [0, 1, 1, 1, 1, 1], 2);
        Assert.Equal(["c", "a"], reranked.Select(d => d.Id)); // id ordinal breaks the tie
    }

    [Fact]
    public void UnknownAuthorityIsUntouched()
    {
        var docs = new List<RetrievedDocument> { Doc("x", 9, 0.01) };
        var reranked = AuthorityBoost.Rerank(docs, AuthorityBoost.DefaultWeights, 1);
        Assert.Equal(0.01, reranked[0].BoostedScore);
    }

    [Fact]
    public void RerankerScoreWinsOverTheServiceScoreWhenTheRankerRan()
    {
        // Azure keeps the two apart: with the semantic ranker on, @search.score
        // is still the fused retrieval score. Ordering on it would discard the
        // ranker's judgment — which is what paying for the ranker buys.
        var docs = new List<RetrievedDocument>
        {
            Doc("fused-winner", 3, 0.030) with { RerankerScore = 1.20 },
            Doc("ranker-winner", 3, 0.010) with { RerankerScore = 2.80 },
        };
        var reranked = AuthorityBoost.Rerank(docs, [0, 1, 1, 1, 1, 1], 2);
        Assert.Equal(["ranker-winner", "fused-winner"], reranked.Select(d => d.Id));
        Assert.Equal(2.80, reranked[0].BoostedScore);
    }

    [Fact]
    public void AuthorityStillWeightsTheRerankerScore()
    {
        var docs = new List<RetrievedDocument>
        {
            Doc("menu", 5, 0.02) with { RerankerScore = 2.00 },
            Doc("product", 1, 0.01) with { RerankerScore = 1.90 },
        };
        var reranked = AuthorityBoost.Rerank(docs, AuthorityBoost.DefaultWeights, 2);
        Assert.Equal("product", reranked[0].Id); // 1.90 * 1.10 > 2.00 * 0.90
    }
}

public class CitationFormatterTests
{
    private static readonly IReadOnlyList<RetrievedDocument> Sources =
    [
        TestDocs.Product("First Product"),
        TestDocs.Pdsrg("Guide Section"),
        TestDocs.Qa("customer q"),
    ];

    [Fact]
    public void ExtractsDistinctMarkersInOrder()
    {
        Assert.Equal([2, 1], CitationFormatter.ExtractMarkers("see [2] and [1], then [2] again"));
        Assert.Equal([], CitationFormatter.ExtractMarkers("no citations here"));
    }

    [Fact]
    public void OutOfRangeMarkersAreDetected()
    {
        Assert.Equal([4, 0], CitationFormatter.OutOfRange("[4] [0] [2]", 3));
    }

    [Fact]
    public void GroupedMarkersAreFlattened()
    {
        // Models reach for these forms unprompted; a marker the parser cannot
        // see is one the post-check counts as missing, failing a good answer.
        Assert.Equal([1, 2], CitationFormatter.ExtractMarkers("both agree [1, 2]"));
        Assert.Equal([1, 2], CitationFormatter.ExtractMarkers("both agree [1;2]"));
        Assert.Equal([1, 2, 3], CitationFormatter.ExtractMarkers("all three [1-3]"));
        Assert.Equal([2, 4, 5], CitationFormatter.ExtractMarkers("mixed [2, 4-5]"));
        Assert.Equal([1, 2], CitationFormatter.ExtractMarkers("en dash [1–2]"));
    }

    [Fact]
    public void GroupedMarkersMapToSourcesAndStayDistinct()
    {
        var citations = CitationFormatter.Collect("supported [1, 2] and again [2]", Sources);
        Assert.Equal([1, 2], citations.Select(c => c.Index));
        Assert.Equal("First Product", citations[0].Title);
    }

    [Fact]
    public void FourDigitBracketsAreNotMarkers()
    {
        // A bracketed year (range) is not a citation group.
        Assert.Equal([], CitationFormatter.ExtractMarkers("published [2020-2021]"));
    }

    [Fact]
    public void CollectMapsMarkersToSources()
    {
        var citations = CitationFormatter.Collect("answer [2] then [1]", Sources);
        Assert.Equal(2, citations.Count);
        Assert.Equal("Guide Section", citations[0].Title);
        Assert.Equal("First Product", citations[1].Title);
        Assert.Contains("practitioner guide", citations[0].Line);
        Assert.Contains("https://example.com/products/test", citations[1].Line);
    }

    [Fact]
    public void RenderBlockNumbersLines()
    {
        string block = CitationFormatter.RenderBlock(CitationFormatter.Collect("[1]", Sources));
        Assert.StartsWith("Sources:", block);
        Assert.Contains("[1] First Product — product copy · https://example.com/products/test", block);
    }

    [Fact]
    public void PodcastCitationShowsLocator()
    {
        var podcast = new RetrievedDocument
        {
            Id = "pod-1", SourceType = "podcast", Authority = 4, Title = "Episode 12",
            Content = "spoken words", Locator = "12:34", IsCurrent = true, Score = 0.01,
        };
        var citations = CitationFormatter.Collect("as said [1]", [podcast]);
        Assert.Contains("12:34", citations[0].Line);
    }
}
