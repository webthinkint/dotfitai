using DotFit.Agents.Config;

namespace DotFit.Agentic.Config;

/// <summary>
/// The knobs the loop runs on (design §6 "Budgets", §7 <c>top</c>).
///
/// Every value here is a guess that the turn log is expected to replace (open
/// item 5), which is why they are options rather than constants: the first
/// owner sessions produce a real distribution of tool calls per turn and wall
/// clock per turn, and those numbers land here without touching the loop.
///
/// The budgets are not a safety mechanism. Nothing on this branch gates
/// (decision D3) — they exist so one pathological turn cannot hold a request
/// open until the service's own 120 s timeout kills it, and so a model that
/// loops on a fruitless search gives up and answers with what it has.
/// </summary>
public sealed record AgenticOptions
{
    public const string MaxToolCallsVar = "DOTFIT_AGENTIC_MAX_TOOL_CALLS";
    public const string TurnTimeoutVar = "DOTFIT_AGENTIC_TURN_TIMEOUT_SECONDS";
    public const string DefaultTopVar = "DOTFIT_AGENTIC_DEFAULT_TOP";

    /// <summary>
    /// Tool calls allowed in one turn (§6). On exhaustion the model is not cut
    /// off: the next tool returns a result telling it to answer with what it
    /// has, so the turn ends in prose rather than in a truncated sentence.
    /// </summary>
    public int MaxToolCalls { get; init; } = 8;

    /// <summary>
    /// Wall clock for one turn (§6), below the service's 120 s request timeout
    /// so the budget — which ends in an answer — wins the race against the
    /// timeout, which ends in a handoff.
    ///
    /// It must also stay below <see cref="HardTimeout"/>, which is capped at
    /// 110 s: above that the ceiling would sit *under* the research budget, the
    /// budget refusal could never fire, and every long turn would end in the
    /// handoff instead of in an answer. <see cref="MaxTurnTimeoutSeconds"/> is
    /// where that is enforced.
    /// </summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The ceiling on the whole turn, in seconds, before the doubling rule
    /// clamps it. Named because two other limits are stated against it.
    /// </summary>
    public const int HardTimeoutSeconds = 110;

    /// <summary>
    /// The largest research budget that keeps <c>TurnTimeout &lt; HardTimeout</c>:
    /// one second under the ceiling, so the budget always wins the race.
    /// </summary>
    public const int MaxTurnTimeoutSeconds = HardTimeoutSeconds - 1;

    /// <summary>
    /// The ceiling on the whole turn, including the answer the model writes
    /// after its last tool call. <see cref="TurnTimeout"/> stops *research*;
    /// this stops everything, and the gap between them is deliberate — a turn
    /// cancelled at 60 s while composing would throw away an answer that was
    /// nearly written. Capped below the service's 120 s request timeout so the
    /// loop always loses to itself before it loses to the host.
    /// </summary>
    public TimeSpan HardTimeout =>
        TimeSpan.FromSeconds(Math.Min(TurnTimeout.TotalSeconds * 2, HardTimeoutSeconds));

    /// <summary>
    /// Default <c>top</c> for the search tool (§7.1). Six rather than v1's
    /// eight: the model can search again, so each search should be cheap.
    /// </summary>
    public int DefaultTop { get; init; } = 6;

    /// <summary>
    /// The caller-facing cap on <c>top</c>, in one place. The service rejects a
    /// larger <c>top</c> with a 400 against this same number — two independent
    /// 20s would drift, and the customer-visible symptom of the drift would be
    /// a request accepted at the edge and then silently clamped.
    /// </summary>
    public const int MaxTopCeiling = 20;

    /// <summary>Ceiling on <c>top</c>, matching the service's caller-facing cap.</summary>
    public int MaxTop { get; init; } = MaxTopCeiling;

    /// <summary>
    /// Characters of a single source's content handed to the model. A PDSRG
    /// table chunk can be long; the cap keeps one oversize source from eating
    /// the context the other five needed. A truncated source says so, and
    /// <c>fetch</c> (§7.2) is how the model gets the rest.
    /// </summary>
    public int MaxSourceChars { get; init; } = 6_000;

    /// <summary>Read the optional overrides out of an already-parsed .env.</summary>
    public static AgenticOptions FromValues(IReadOnlyDictionary<string, string> values)
    {
        int Int(string name, int fallback, int min, int max)
        {
            if (!values.TryGetValue(name, out string? raw) || raw.Trim().Length == 0)
                return fallback;
            if (!int.TryParse(raw.Trim(), out int parsed) || parsed < min || parsed > max)
                throw new EnvFile.EnvFileException(
                    $"{EnvFile.FileName}: {name} must be an integer between {min} and {max}");
            return parsed;
        }

        // Fallbacks read off a default instance rather than repeated as
        // literals. Repeating them made the property initializers dead on every
        // real path — the service and the CLI both load through here — so
        // changing a default changed nothing that shipped, which is a trap for
        // exactly the edit open item 5 exists to make.
        var defaults = new AgenticOptions();

        var options = new AgenticOptions
        {
            MaxToolCalls = Int(MaxToolCallsVar, defaults.MaxToolCalls, 1, 64),
            TurnTimeout = TimeSpan.FromSeconds(Int(
                TurnTimeoutVar, (int)defaults.TurnTimeout.TotalSeconds, 5, MaxTurnTimeoutSeconds)),
            DefaultTop = Int(DefaultTopVar, defaults.DefaultTop, 1, defaults.MaxTop),
        };
        if (options.DefaultTop > options.MaxTop)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {DefaultTopVar} must not exceed {options.MaxTop}");
        return options;
    }

    /// <summary>Load beside the <c>.env</c> the rest of the runtime reads.</summary>
    public static AgenticOptions Load(string envFilePath) =>
        FromValues(EnvFile.ReadFile(envFilePath));

    public override string ToString() =>
        $"AgenticOptions(max_tool_calls={MaxToolCalls}, turn_timeout={TurnTimeout.TotalSeconds:0}s, " +
        $"default_top={DefaultTop}, max_top={MaxTop}, max_source_chars={MaxSourceChars})";
}
