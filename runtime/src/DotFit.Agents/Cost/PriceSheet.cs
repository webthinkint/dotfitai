using System.Globalization;
using DotFit.Agents.Config;

namespace DotFit.Agents.Cost;

/// <summary>
/// The per-unit prices a turn's usage is priced against — the sheet behind
/// the <c>cost</c> block both runtimes put on their <c>result</c> event and in
/// their logs (agentic §9/§10; v1's integration doc and verdict log).
///
/// The money on the wire is **derived, not observed**: usage counts come back
/// from the APIs, and this sheet turns them into currency. That makes the
/// numbers exactly as current as the sheet — which is why every cost block
/// carries <see cref="Id"/> verbatim, so a number read six months from now is
/// interpretable against the prices that produced it. When a deployment or a
/// price changes, the fix is a <c>.env</c> edit and a new <see cref="Id"/>,
/// never code.
///
/// The defaults are placeholders, not research: the chat trio mirrors the
/// current frontier list prices, the embedding price is
/// <c>text-embedding-3-large</c>'s, and the search price is a **dummy** —
/// Azure AI Search on a provisioned tier bills the month, not the query, so
/// there is no per-query price to read; the dummy stands in until a real
/// cost-per-query is derived from the service's own billing data. The counts
/// (<c>index_queries</c>, <c>ranker_queries</c>) are exact either way.
///
/// The small-chat trio has no independent defaults: **unset small-chat prices
/// fall back to the chat trio's configured values**, because the deployment
/// this repo currently points <c>SMALL_CHAT</c> at is the same frontier model
/// as <c>CHAT</c> — builtin cheap-model defaults would silently understate,
/// and builtin frontier defaults would silently overstate, every v1 turn the
/// moment real chat prices are configured. Set the three apart the day the
/// deployments actually differ; `/healthz` on either service shows the
/// effective prices either way.
/// </summary>
public sealed record PriceSheet
{
    public const string ChatInputVar = "DOTFIT_PRICE_CHAT_INPUT_PER_1M";
    public const string ChatCachedInputVar = "DOTFIT_PRICE_CHAT_CACHED_INPUT_PER_1M";
    public const string ChatOutputVar = "DOTFIT_PRICE_CHAT_OUTPUT_PER_1M";
    public const string SmallChatInputVar = "DOTFIT_PRICE_SMALL_CHAT_INPUT_PER_1M";
    public const string SmallChatCachedInputVar = "DOTFIT_PRICE_SMALL_CHAT_CACHED_INPUT_PER_1M";
    public const string SmallChatOutputVar = "DOTFIT_PRICE_SMALL_CHAT_OUTPUT_PER_1M";
    public const string EmbeddingVar = "DOTFIT_PRICE_EMBEDDING_PER_1M";
    public const string SearchVar = "DOTFIT_PRICE_SEARCH_PER_1K";
    public const string CurrencyVar = "DOTFIT_PRICE_CURRENCY";
    public const string IdVar = "DOTFIT_PRICE_SHEET";

    /// <summary>
    /// Identifies the prices behind every cost number emitted — the default is
    /// honest about being the built-in placeholder. Override it whenever the
    /// prices are, so a turn log from two months ago still means something.
    /// </summary>
    public string Id { get; init; } = "builtin-2026-09";

    public string Currency { get; init; } = "USD";

    public decimal ChatInputPerMillion { get; init; } = 1.25m;

    /// <summary>
    /// Cached prompt tokens are billed at a discount on Foundry/Azure OpenAI,
    /// and a multi-round-trip turn re-sends the same context every round trip
    /// — on exactly the deployments where the discount applies. Priced
    /// separately or the turn log would overstate every tool-calling turn,
    /// which is most of them.
    /// </summary>
    public decimal ChatCachedInputPerMillion { get; init; } = 0.125m;

    public decimal ChatOutputPerMillion { get; init; } = 10m;

    public decimal SmallChatInputPerMillion { get; init; } = 1.25m;
    public decimal SmallChatCachedInputPerMillion { get; init; } = 0.125m;
    public decimal SmallChatOutputPerMillion { get; init; } = 10m;

    /// <summary>Query embedding — <c>text-embedding-3-large</c>'s list price.</summary>
    public decimal EmbeddingPerMillion { get; init; } = 0.13m;

    /// <summary>
    /// Per *thousand* index queries: one unit per query-plane call the runtime
    /// makes (hybrid search, <c>get_product</c>'s filter query, <c>fetch</c>'s
    /// key lookups, v1's single hybrid search). A placeholder until real cost
    /// data replaces it.
    /// </summary>
    public decimal SearchPerThousand { get; init; } = 0.25m;

    /// <summary>Read the optional overrides out of an already-parsed .env.</summary>
    public static PriceSheet FromValues(IReadOnlyDictionary<string, string> values)
    {
        decimal Parse(string name, decimal fallback)
        {
            if (!values.TryGetValue(name, out string? raw) || raw.Trim().Length == 0)
                return fallback;
            if (!decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
                || parsed < 0)
                throw new EnvFile.EnvFileException(
                    $"{EnvFile.FileName}: {name} must be a non-negative number (use '.' as the decimal point)");
            return parsed;
        }

        string Text(string name, string fallback) =>
            values.TryGetValue(name, out string? raw) && raw.Trim().Length > 0 ? raw.Trim() : fallback;

        var defaults = new PriceSheet();
        decimal chatInput = Parse(ChatInputVar, defaults.ChatInputPerMillion);
        decimal chatCached = Parse(ChatCachedInputVar, defaults.ChatCachedInputPerMillion);
        decimal chatOutput = Parse(ChatOutputVar, defaults.ChatOutputPerMillion);
        return new PriceSheet
        {
            ChatInputPerMillion = chatInput,
            ChatCachedInputPerMillion = chatCached,
            ChatOutputPerMillion = chatOutput,
            // Unset small-chat prices inherit the chat trio's *configured*
            // values, not the builtin defaults — see the type remarks.
            SmallChatInputPerMillion = Parse(SmallChatInputVar, chatInput),
            SmallChatCachedInputPerMillion = Parse(SmallChatCachedInputVar, chatCached),
            SmallChatOutputPerMillion = Parse(SmallChatOutputVar, chatOutput),
            EmbeddingPerMillion = Parse(EmbeddingVar, defaults.EmbeddingPerMillion),
            SearchPerThousand = Parse(SearchVar, defaults.SearchPerThousand),
            Currency = Text(CurrencyVar, defaults.Currency).ToUpperInvariant(),
            Id = Text(IdVar, defaults.Id),
        };
    }

    /// <summary>Load beside the <c>.env</c> the rest of the runtime reads.</summary>
    public static PriceSheet Load(string envFilePath) => FromValues(EnvFile.ReadFile(envFilePath));

    public override string ToString() =>
        $"PriceSheet({Id}, {Currency}: chat {ChatInputPerMillion.ToString(CultureInfo.InvariantCulture)} in / " +
        $"{ChatCachedInputPerMillion.ToString(CultureInfo.InvariantCulture)} cached / " +
        $"{ChatOutputPerMillion.ToString(CultureInfo.InvariantCulture)} out per 1M tok, " +
        $"small chat {SmallChatInputPerMillion.ToString(CultureInfo.InvariantCulture)} in / " +
        $"{SmallChatCachedInputPerMillion.ToString(CultureInfo.InvariantCulture)} cached / " +
        $"{SmallChatOutputPerMillion.ToString(CultureInfo.InvariantCulture)} out per 1M tok, " +
        $"embedding {EmbeddingPerMillion.ToString(CultureInfo.InvariantCulture)} per 1M tok, " +
        $"search {SearchPerThousand.ToString(CultureInfo.InvariantCulture)} per 1k queries)";
}
