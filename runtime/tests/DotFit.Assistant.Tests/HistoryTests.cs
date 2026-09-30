namespace DotFit.Assistant.Tests;

/// <summary>
/// Conversation history normalization. The caller sends history with every
/// request; the bound on what reaches the model is ours, not the caller's.
/// </summary>
public class HistoryTests
{
    private static ConversationTurn User(string text) => new(ConversationRole.User, text);
    private static ConversationTurn Bot(string text) => new(ConversationRole.Assistant, text);

    [Fact]
    public void HistoryKeepsOnlyTheMostRecentTurns()
    {
        // A relay that ships a whole transcript must not turn one question into
        // a prompt that costs more than the answer.
        List<ConversationTurn> sent = Enumerable.Range(1, 30)
            .Select(i => i % 2 == 1 ? User($"q{i}") : Bot($"a{i}")).ToList();

        IReadOnlyList<ConversationTurn> kept = ConversationHistory.Normalize(sent, "current?");

        Assert.Equal(ConversationHistory.MaxTurns, kept.Count);
        Assert.Equal("q23", kept[0].Text);          // the oldest kept, not the oldest sent
        Assert.Equal("a30", kept[^1].Text);
    }

    [Fact]
    public void LongTurnsAreHeadTruncated()
    {
        // A turn states its topic up front, so the head is the part worth
        // keeping — and an assistant answer with citations is long.
        var turn = Bot(new string('x', ConversationHistory.MaxTurnChars + 500));

        ConversationTurn kept = ConversationHistory.Normalize([turn], "q?").Single();

        Assert.Equal(ConversationHistory.MaxTurnChars + 1, kept.Text.Length);   // + the ellipsis
        Assert.EndsWith("…", kept.Text);
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
