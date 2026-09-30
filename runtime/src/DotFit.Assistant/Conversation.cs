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
/// </summary>
public static class ConversationHistory
{
    /// <summary>Most recent turns kept — four exchanges, enough to resolve a follow-up.</summary>
    public const int MaxTurns = 8;

    /// <summary>Characters kept per turn. Head-truncated: a turn states its topic up front.</summary>
    public const int MaxTurnChars = 1000;

    public static readonly IReadOnlyList<ConversationTurn> Empty = [];

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
    /// keep the newest <see cref="MaxTurns"/>, truncate each to
    /// <see cref="MaxTurnChars"/>.
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

        if (kept.Count > MaxTurns)
            kept.RemoveRange(0, kept.Count - MaxTurns);

        for (int i = 0; i < kept.Count; i++)
        {
            if (kept[i].Text.Length > MaxTurnChars)
                kept[i] = kept[i] with { Text = kept[i].Text[..MaxTurnChars].TrimEnd() + "…" };
        }

        return kept.Count == 0 ? Empty : kept;
    }
}
