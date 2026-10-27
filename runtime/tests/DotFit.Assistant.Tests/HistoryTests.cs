namespace DotFit.Assistant.Tests;

/// <summary>
/// Conversation history normalization. The caller sends history with every
/// request; the bound on what reaches the model is ours, not the caller's.
/// </summary>
public class HistoryTests
{
    private static ConversationTurn User(string text) => new(ConversationRole.User, text);
    private static ConversationTurn Bot(string text) => new(ConversationRole.Assistant, text);

    private static int Tokens(IEnumerable<ConversationTurn> turns) =>
        turns.Sum(t => ConversationHistory.EstimateTokens(t.Text));

    /// <summary>A conversation of <paramref name="exchanges"/> questions, each answered at length.</summary>
    private static List<ConversationTurn> Conversation(int exchanges, int answerChars = 2_000) =>
        Enumerable.Range(1, exchanges)
            .SelectMany(i => new[] { User($"q{i}"), Bot($"a{i} " + new string('x', answerChars)) })
            .ToList();

    [Fact]
    public void HistoryUnderTheBudgetIsKeptWhole()
    {
        // A budget, not a window: a trigger stated fifteen turns back still binds.
        List<ConversationTurn> sent = Enumerable.Range(1, 30)
            .Select(i => i % 2 == 1 ? User($"q{i}") : Bot($"a{i}")).ToList();

        Assert.Equal(sent, ConversationHistory.Normalize(sent, "current?"));
    }

    [Fact]
    public void LongTurnsAreHeadTruncated()
    {
        // A turn states its topic up front, so the head is the part worth keeping.
        int cap = ConversationHistory.MaxTurnTokens * ConversationHistory.CharsPerToken;
        var turn = Bot(new string('x', cap + 500));

        ConversationTurn kept = ConversationHistory.Normalize([turn], "q?").Single();

        Assert.Equal(cap + 1, kept.Text.Length);   // + the ellipsis
        Assert.EndsWith("…", kept.Text);
    }

    [Fact]
    public void OverTheBudgetOldAnswersAreShortenedAndEveryQuestionKept()
    {
        List<ConversationTurn> sent = Conversation(40);

        IReadOnlyList<ConversationTurn> kept = ConversationHistory.Normalize(sent, "current?");

        Assert.Equal(sent.Where(t => t.Role == ConversationRole.User), kept.Where(t => t.Role == ConversationRole.User));
        Assert.Equal(sent.Count, kept.Count);                  // shortened, nothing dropped
        Assert.StartsWith("a1 ", kept[1].Text);
        Assert.EndsWith("…", kept[1].Text);
        Assert.Equal(sent[^1], kept[^1]);                      // the newest answer is whole
        Assert.True(Tokens(kept) <= ConversationHistory.HistoryBudgetTokens);
    }

    [Fact]
    public void ExchangesAreDroppedWholeOnlyWhenShorteningIsNotEnough()
    {
        // Long questions leave nothing to shorten, so the oldest exchanges go,
        // and a question never survives without its answer.
        List<ConversationTurn> sent = Enumerable.Range(1, 40)
            .SelectMany(i => new[] { User($"q{i} " + new string('y', 2_000)), Bot($"a{i}") })
            .ToList();

        IReadOnlyList<ConversationTurn> kept = ConversationHistory.Normalize(sent, "current?");

        Assert.True(kept.Count < sent.Count);
        Assert.Equal(ConversationRole.User, kept[0].Role);
        Assert.Equal(sent[^2..], kept.TakeLast(2));
        Assert.True(Tokens(kept) <= ConversationHistory.HistoryBudgetTokens);
    }

    [Fact]
    public void HistoryIsPrefixStableBetweenCuts()
    {
        // The prompt cache only helps if each request's history begins with the
        // previous request's history plus its question, byte for byte. Cuts
        // break that, so they must be rare: a jump to half the budget, not a
        // slide on every turn.
        List<ConversationTurn> full = Conversation(120);
        int breaks = 0;
        for (int n = 1; n < 120; n++)
        {
            IReadOnlyList<ConversationTurn> before = ConversationHistory.Normalize(full[..(2 * n - 2)], $"q{n}");
            IReadOnlyList<ConversationTurn> after = ConversationHistory.Normalize(full[..(2 * n)], $"q{n + 1}");
            List<ConversationTurn> sentBefore = [.. before, User($"q{n}")];
            if (!after.Take(sentBefore.Count).SequenceEqual(sentBefore))
                breaks++;
        }

        Assert.InRange(breaks, 1, 120 / 10);
    }

    [Fact]
    public void ATrailingEchoOfTheCurrentQuestionIsDropped()
    {
        // A caller that appends the turn to its transcript *before* calling us
        // would otherwise send the question as its own context, and the
        // model would read the repetition as the customer asking twice.
        IReadOnlyList<ConversationTurn> kept = ConversationHistory.Normalize(
            [User("is LeanMeal good for weight loss?"), Bot("It is [1]."), User("  What About The Chocolate One? ")],
            "what about the chocolate one?");

        Assert.Equal(2, kept.Count);
        Assert.Equal(ConversationRole.Assistant, kept[^1].Role);
    }

    [Fact]
    public void BlankTurnsAreDroppedAndTextIsTrimmed()
    {
        IReadOnlyList<ConversationTurn> kept = ConversationHistory.Normalize(
            [User("  first  "), Bot("   "), Bot("second")], "q?");

        Assert.Equal(["first", "second"], kept.Select(t => t.Text));
    }

    [Fact]
    public void NoHistoryIsTheDefaultAndStaysEmpty()
    {
        Assert.Empty(ConversationHistory.Normalize(null, "q?"));
        Assert.Empty(ConversationHistory.Normalize([Bot("  ")], "q?"));
    }
}
