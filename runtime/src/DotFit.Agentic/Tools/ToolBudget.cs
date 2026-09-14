using System.Diagnostics;

namespace DotFit.Agentic.Tools;

/// <summary>
/// The turn's tool-call and wall-clock budget (design §6).
///
/// The refusal is prose, not an exception, and that is the design: a model told
/// "you have no searches left, answer with what you have" finishes its turn in
/// a sentence, while a model whose tool call throws produces an error the
/// customer sees. Nothing here gates content — the budget bounds cost, not
/// speech (decision D3).
///
/// The numbers are guesses awaiting the turn log (open item 5).
/// </summary>
public sealed class ToolBudget(int maxCalls, TimeSpan timeout)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _used;

    public int MaxCalls { get; } = maxCalls;
    public int Used => Volatile.Read(ref _used);
    public TimeSpan Elapsed => _clock.Elapsed;
    public long ElapsedMs => _clock.ElapsedMilliseconds;

    /// <summary>True once either budget ended the turn's tool use.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>
    /// Claim one call. Returns false with a model-facing explanation when the
    /// budget is spent; the counter is still incremented so a model that keeps
    /// trying cannot spin for free.
    /// </summary>
    public bool TryConsume(out string refusal)
    {
        // Counted first, so *both* refusals consume a call. Checking the clock
        // before incrementing made the time refusal free: between the research
        // budget expiring and the hard ceiling, a model that kept calling tools
        // got an instant refusal each time — and each refusal is a full paid
        // round trip carrying the whole conversation. With the count advancing,
        // the call-count refusal takes over from the time refusal for a model
        // that keeps trying.
        int used = Interlocked.Increment(ref _used);

        if (_clock.Elapsed > timeout)
        {
            Exhausted = true;
            refusal =
                $"This turn has used its {timeout.TotalSeconds:0}-second research budget. Do not call any more " +
                "tools. Answer now with the sources you already have, and say plainly what you could not look up.";
            return false;
        }

        if (used > MaxCalls)
        {
            Exhausted = true;
            refusal =
                $"This turn has used all {MaxCalls} of its tool calls. Do not call any more tools. Answer now " +
                "with the sources you already have, and say plainly what you could not look up.";
            return false;
        }

        refusal = "";
        return true;
    }
}
