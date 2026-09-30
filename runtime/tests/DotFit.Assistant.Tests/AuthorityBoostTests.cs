using DotFit.Assistant.Retrieval;

namespace DotFit.Assistant.Tests;

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
