using System.Text.Json.Serialization;
using DotFit.Agentic.Config;
using DotFit.Agents.Retrieval;

namespace DotFit.Agentic.Turn;

/// <summary>
/// What one turn cost, as the caller (§9) and the turn log (§10) see it.
///
/// The counts are observed — token counts off the model's own usage reports,
/// embedding counts off the embedding API's, index-query counts off the calls
/// the tools actually made — and the money is those counts priced against a
/// <see cref="PriceSheet"/>. No rounding happens here; a cost is a decimal sum
/// of exact products, and display precision belongs to whoever displays it.
///
/// The search line is priced off a **dummy** per-query rate (see
/// <see cref="PriceSheet.SearchPerThousand"/>): a provisioned AI Search tier
/// bills the month, not the query, so the honest per-turn search cost on the
/// current setup is the amortized share of a fixed bill — unknowable per turn
/// and estimated at best. The counts are exact, the rate is a placeholder
/// until real billing data replaces it, and every emitted block says which
/// sheet priced it.
/// </summary>
public sealed record TurnCost
{
    [JsonPropertyName("currency")] public required string Currency { get; init; }
    /// <summary>The <see cref="PriceSheet.Id"/> that priced this turn, verbatim.</summary>
    [JsonPropertyName("price_sheet")] public required string PriceSheet { get; init; }

    [JsonPropertyName("chat_input_tokens")] public required long ChatInputTokens { get; init; }
    /// <summary>
    /// The cached-input subset of <see cref="ChatInputTokens"/>, summed across
    /// round trips — the part billed at the discounted rate.
    /// </summary>
    [JsonPropertyName("chat_cached_input_tokens")] public required long ChatCachedInputTokens { get; init; }
    [JsonPropertyName("chat_output_tokens")] public required long ChatOutputTokens { get; init; }

    [JsonPropertyName("embedding_calls")] public required int EmbeddingCalls { get; init; }
    [JsonPropertyName("embedding_tokens")] public required long EmbeddingTokens { get; init; }

    /// <summary>Query-plane calls made against the index this turn.</summary>
    [JsonPropertyName("index_queries")] public required int IndexQueries { get; init; }
    /// <summary>
    /// Of which ran the semantic ranker. Reported separately because it is the
    /// one AI Search line item that is metered per query — a future sheet may
    /// price it apart from the dummy flat rate.
    /// </summary>
    [JsonPropertyName("ranker_queries")] public required int RankerQueries { get; init; }

    [JsonPropertyName("chat_usd")] public required decimal ChatUsd { get; init; }
    [JsonPropertyName("embedding_usd")] public required decimal EmbeddingUsd { get; init; }
    [JsonPropertyName("search_usd")] public required decimal SearchUsd { get; init; }

    [JsonPropertyName("total_usd")] public decimal TotalUsd => ChatUsd + EmbeddingUsd + SearchUsd;
}

/// <summary>
/// Accumulates the usage a turn's cost is priced from. One instance per turn,
/// shared by the loop (chat usage, from the model's own reports) and the tools
/// (embedding usage and index queries) — the Agent Framework may invoke tools
/// concurrently, so every mutation takes the same lock the ledger does.
///
/// Deliberately counts, not costs: the money is derived once, at result time,
/// against the sheet the assistant was built with, so a mid-turn price change
/// is impossible and the <c>cost</c> block is always a function of one sheet.
/// </summary>
public sealed class TurnMeter : IEmbeddingUsageSink
{
    private readonly Lock _gate = new();
    private long _chatInput;
    private long _chatCached;
    private long _chatOutput;
    private long _embeddingTokens;
    private int _embeddingCalls;
    private int _indexQueries;
    private int _rankerQueries;
    private bool _chatReported;

    /// <summary>False until some round trip reported usage — keeps token fields null, not zero.</summary>
    public bool ChatReported { get { lock (_gate) return _chatReported; } }

    /// <summary>
    /// One round trip's usage, each count optional — the same permissiveness
    /// the loop's old locals had: input and output are summed independently,
    /// so a report carrying only one of them still contributes its half.
    /// </summary>
    public void Chat(long? input, long? cached, long? output)
    {
        lock (_gate)
        {
            if (input is { } i) { _chatInput += i; _chatReported = true; }
            if (cached is { } c) _chatCached += c;
            if (output is { } o) { _chatOutput += o; _chatReported = true; }
        }
    }

    /// <summary>One query embedding, with the input tokens the API reported.</summary>
    public void Embedding(long inputTokens)
    {
        lock (_gate)
        {
            _embeddingTokens += inputTokens;
            _embeddingCalls++;
        }
    }

    /// <summary>
    /// Index query-plane calls served — hybrid searches, filter queries, key
    /// lookups; a 404 lookup counts, a transport failure does not (Azure was
    /// asked either way, but only served requests are billed anything).
    /// </summary>
    public void IndexQuery(int count = 1)
    {
        lock (_gate) _indexQueries += count;
    }

    public void RankerQuery()
    {
        lock (_gate) _rankerQueries++;
    }

    public TurnCost Cost(PriceSheet sheet)
    {
        long chatInput, chatCached, chatOutput, embeddingTokens;
        int embeddingCalls, indexQueries, rankerQueries;
        lock (_gate)
        {
            chatInput = _chatInput;
            chatCached = _chatCached;
            chatOutput = _chatOutput;
            embeddingTokens = _embeddingTokens;
            embeddingCalls = _embeddingCalls;
            indexQueries = _indexQueries;
            rankerQueries = _rankerQueries;
        }

        // Cached is a subset of input on the same round trip; clamped rather
        // than trusted, so a provider reporting an inconsistent pair cannot
        // mint negative tokens into the money.
        long uncached = Math.Max(0, chatInput - chatCached);
        return new TurnCost
        {
            Currency = sheet.Currency,
            PriceSheet = sheet.Id,
            ChatInputTokens = chatInput,
            ChatCachedInputTokens = chatCached,
            ChatOutputTokens = chatOutput,
            EmbeddingCalls = embeddingCalls,
            EmbeddingTokens = embeddingTokens,
            IndexQueries = indexQueries,
            RankerQueries = rankerQueries,
            ChatUsd =
                uncached * sheet.ChatInputPerMillion / 1_000_000m
                + chatCached * sheet.ChatCachedInputPerMillion / 1_000_000m
                + chatOutput * sheet.ChatOutputPerMillion / 1_000_000m,
            EmbeddingUsd = embeddingTokens * sheet.EmbeddingPerMillion / 1_000_000m,
            SearchUsd = indexQueries * sheet.SearchPerThousand / 1_000m,
        };
    }
}
