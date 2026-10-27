namespace DotFit.Assistant;

/// <summary>Who spoke a turn. The wire spells these <c>user</c> / <c>assistant</c>.</summary>
public enum ConversationRole
{
    User,
    Assistant,
}

/// <summary>
/// One earlier turn of the same conversation. The
/// caller's database is the system of record — the runtime holds no state
/// between requests — so history arrives with each question rather than being
/// looked up.
/// </summary>
public sealed record ConversationTurn(ConversationRole Role, string Text);

/// <summary>
/// What the runtime will accept as conversation history, and the one place
/// that decides it.
///
/// History is a caller-supplied, unbounded input to the model, so the bound is
/// ours and not theirs: a relay that sends a whole transcript must not turn one
/// question into a prompt that costs more than the answer. The trims are
/// deterministic and testable.
///
/// History reaches the model as earlier user and assistant messages: text
/// only, no tool calls and no sources. A trigger stated earlier (an age, a
/// pregnancy) therefore still binds, and an earlier answer's citations are not
/// sources the model may cite again.
///
/// The bound is a budget, not a window. Every user turn is kept, because a
/// trigger can sit in any of them and only a model could tell which. When the
/// history outgrows <see cref="HistoryBudgetTokens"/>, the oldest exchanges are
/// shortened to their user turn plus the head of the answer, which keeps what
/// was said without what was listed, until it fits
/// <see cref="HistoryCutToTokens"/>. Only if that is not enough are the oldest
/// exchanges dropped, whole, so a user turn never survives without its answer.
/// The newest exchange is never cut: it is what a follow-up is about.
///
/// The trims are also prefix-stable, which is what lets a deployment read
/// history from its prompt cache: a request's history is the previous
/// request's, plus its question and answer, byte for byte. A turn's kept text
/// never depends on its position, and the cut points are replayed exchange by
/// exchange as the conversation grew, so they move in jumps — budget to half
/// budget — rather than on every turn. A cut costs one uncached turn; the
/// turns between cuts reuse the cache.
/// </summary>
public static class ConversationHistory
{
    /// <summary>
    /// The token estimate's ratio, about right for English. An estimate, because
    /// Claude has no local tokenizer and counting through the API is a round trip.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Estimated tokens kept per turn, head-truncated: a turn states its topic up
    /// front. Room for a whole menu or program answer, and for any question the
    /// service accepts, so a question reaches its next turn's history uncut.
    /// </summary>
    public const int MaxTurnTokens = 3_000;

    /// <summary>Estimated tokens kept of an answer once its exchange is shortened.</summary>
    public const int ShortenedAnswerTokens = 75;

    /// <summary>History above this many estimated tokens is cut back…</summary>
    public const int HistoryBudgetTokens = 16_000;

    /// <summary>…to this many, in one go, so the next cut is many turns away.</summary>
    public const int HistoryCutToTokens = 8_000;

    public static readonly IReadOnlyList<ConversationTurn> Empty = [];

    /// <summary>The estimated token count of a text.</summary>
    public static int EstimateTokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    /// <summary>
    /// Wire <c>role</c> → <see cref="ConversationRole"/>, case-insensitively.
    /// An unknown role is rejected rather than dropped: a transcript with a
    /// hole in it silently changes what the conversation says.
    /// </summary>
    public static bool TryParseRole(string? role, out ConversationRole parsed)
    {
        switch (role?.Trim().ToLowerInvariant())
        {
            case "user": parsed = ConversationRole.User; return true;
            case "assistant": parsed = ConversationRole.Assistant; return true;
            default: parsed = ConversationRole.User; return false;
        }
    }

    /// <summary>
    /// Drop blank turns, drop a trailing echo of the question being asked,
    /// head-truncate each turn to <see cref="MaxTurnTokens"/>, then fit the
    /// budget as the class comment describes.
    ///
    /// The echo rule is defensive, not cosmetic: a relay that appends the turn
    /// to its transcript *before* calling us would otherwise send the current
    /// question as its own context, and the model would read the repetition
    /// as the customer asking twice.
    /// </summary>
    public static IReadOnlyList<ConversationTurn> Normalize(
        IReadOnlyList<ConversationTurn>? history, string currentQuestion)
    {
        if (history is null || history.Count == 0)
            return Empty;

        var kept = new List<ConversationTurn>(history.Count);
        foreach (ConversationTurn turn in history)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
                continue;
            kept.Add(turn with { Text = turn.Text.Trim() });
        }

        string question = currentQuestion.Trim();
        while (kept.Count > 0
               && kept[^1].Role == ConversationRole.User
               && string.Equals(kept[^1].Text, question, StringComparison.OrdinalIgnoreCase))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count == 0)
            return Empty;

        List<Exchange> exchanges = Exchanges(kept.Select(t => t with { Text = HeadCut(t.Text, MaxTurnTokens) }));
        (int dropped, int shortened) = Cuts(exchanges);

        var result = new List<ConversationTurn>(kept.Count);
        for (int i = dropped; i < exchanges.Count; i++)
            result.AddRange(i < shortened ? exchanges[i].Shortened : exchanges[i].Full);
        return result;
    }

    /// <summary>
    /// The cut points: exchanges before <c>Dropped</c> are gone, those before
    /// <c>Shortened</c> are shortened. Replayed from the first exchange, one at a
    /// time as the conversation grew, so a request computes the same cuts its
    /// predecessor did for every exchange they share.
    /// </summary>
    private static (int Dropped, int Shortened) Cuts(IReadOnlyList<Exchange> exchanges)
    {
        int dropped = 0, shortened = 0, total = 0;
        for (int newest = 0; newest < exchanges.Count; newest++)
        {
            total += exchanges[newest].FullTokens;
            if (total <= HistoryBudgetTokens)
                continue;
            while (total > HistoryCutToTokens && shortened < newest)
            {
                total -= exchanges[shortened].FullTokens - exchanges[shortened].ShortenedTokens;
                shortened++;
            }
            // Everything before the newest is shortened by now.
            while (total > HistoryCutToTokens && dropped < newest)
            {
                total -= exchanges[dropped].ShortenedTokens;
                dropped++;
            }
        }
        return (dropped, shortened);
    }

    /// <summary>
    /// A user turn and the answers that followed it. Turns before the first user
    /// turn (a greeting the caller showed) form an exchange of their own.
    /// </summary>
    private static List<Exchange> Exchanges(IEnumerable<ConversationTurn> turns)
    {
        var exchanges = new List<Exchange>();
        List<ConversationTurn>? current = null;
        foreach (ConversationTurn turn in turns)
        {
            if (current is null || turn.Role == ConversationRole.User)
            {
                current = [];
                exchanges.Add(new Exchange(current));
            }
            current.Add(turn);
        }
        return exchanges;
    }

    private sealed class Exchange(List<ConversationTurn> full)
    {
        public List<ConversationTurn> Full { get; } = full;

        public IEnumerable<ConversationTurn> Shortened => Full.Select(t =>
            t.Role == ConversationRole.Assistant ? t with { Text = HeadCut(t.Text, ShortenedAnswerTokens) } : t);

        public int FullTokens => Full.Sum(t => EstimateTokens(t.Text));

        public int ShortenedTokens => Shortened.Sum(t => EstimateTokens(t.Text));
    }

    /// <summary>The head of a text within an estimated token count, elided with "…".</summary>
    private static string HeadCut(string text, int tokens)
    {
        int chars = tokens * CharsPerToken;
        if (text.Length <= chars)
            return text;
        if (char.IsHighSurrogate(text[chars - 1]))
            chars--;                                  // never split a surrogate pair
        return text[..chars].TrimEnd() + "…";
    }
}
