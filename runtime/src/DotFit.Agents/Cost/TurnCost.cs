using System.Text.Json.Serialization;

namespace DotFit.Agents.Cost;

/// <summary>
/// What one turn cost, as the caller (agentic §9) and the logs (agentic §10,
/// v1's verdict log) see it. Shared by both runtimes so their cost blocks are
/// one schema and an A/B dashboard can read them side by side.
///
/// The counts are observed — token counts off the model's own usage reports,
/// embedding counts off the embedding API's, index-query counts off the calls
/// the pipeline actually made — and the money is those counts priced against a
/// <see cref="PriceSheet"/>. No rounding happens here; a cost is a decimal sum
/// of exact products, and display precision belongs to whoever displays it.
///
/// The <c>small_chat</c> block is v1's small-deployment stages (guardrail,
/// rewrite, claims audit, conversational reply). The agentic runtime calls no
/// small model, so it reports the block at zeros — saying "nothing was spent
/// there", which is a different statement from a block being absent. The
/// search line is priced off a **dummy** per-query rate (see
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

    // The main chat deployment — the agentic loop's one model, v1's answer agent.

    [JsonPropertyName("chat_input_tokens")] public required long ChatInputTokens { get; init; }
    /// <summary>
    /// The cached-input subset of <see cref="ChatInputTokens"/>, summed across
    /// round trips — the part billed at the discounted rate.
    /// </summary>
    [JsonPropertyName("chat_cached_input_tokens")] public required long ChatCachedInputTokens { get; init; }
    [JsonPropertyName("chat_output_tokens")] public required long ChatOutputTokens { get; init; }

    // The small chat deployment — v1's guardrail, rewrite, claims audit and
    // conversational reply. Always zero on the agentic runtime.

    [JsonPropertyName("small_chat_input_tokens")] public required long SmallChatInputTokens { get; init; }
    [JsonPropertyName("small_chat_cached_input_tokens")] public required long SmallChatCachedInputTokens { get; init; }
    [JsonPropertyName("small_chat_output_tokens")] public required long SmallChatOutputTokens { get; init; }

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
    [JsonPropertyName("small_chat_usd")] public required decimal SmallChatUsd { get; init; }
    [JsonPropertyName("embedding_usd")] public required decimal EmbeddingUsd { get; init; }
    [JsonPropertyName("search_usd")] public required decimal SearchUsd { get; init; }

    [JsonPropertyName("total_usd")] public decimal TotalUsd =>
        ChatUsd + SmallChatUsd + EmbeddingUsd + SearchUsd;

    /// <summary>
    /// The wire shape both runtimes' SSE <c>result</c> projections emit, in one
    /// place so the two services cannot drift: the website team A/Bs the
    /// runtimes against one contract, and a cost block that differed by
    /// runtime would be two contracts wearing one name. Field names here are
    /// the contract — pinned by tests on both sides.
    /// </summary>
    public object ToWire() => new
    {
        currency = Currency,
        price_sheet = PriceSheet,
        chat = new
        {
            input_tokens = ChatInputTokens,
            cached_input_tokens = ChatCachedInputTokens,
            output_tokens = ChatOutputTokens,
            usd = ChatUsd,
        },
        small_chat = new
        {
            input_tokens = SmallChatInputTokens,
            cached_input_tokens = SmallChatCachedInputTokens,
            output_tokens = SmallChatOutputTokens,
            usd = SmallChatUsd,
        },
        embedding = new
        {
            calls = EmbeddingCalls,
            tokens = EmbeddingTokens,
            usd = EmbeddingUsd,
        },
        search = new
        {
            queries = IndexQueries,
            ranker_queries = RankerQueries,
            usd = SearchUsd,
        },
        total_usd = TotalUsd,
    };
}

/// <summary>
/// Accumulates the usage a turn's cost is priced from. One instance per turn,
/// shared by everything a turn runs — the agentic loop and its tools, v1's
/// pipeline stages — so the result's <c>cost</c> block is one account of the
/// turn. Stage agents may be invoked concurrently by a future caller, so every
/// mutation takes the same kind of lock the agentic ledger does.
///
/// Deliberately counts, not costs: the money is derived once, at result time,
/// against the sheet the caller was built with, so a mid-turn price change is
/// impossible and the <c>cost</c> block is always a function of one sheet.
/// </summary>
public sealed class TurnMeter : Retrieval.IEmbeddingUsageSink
{
    private readonly Lock _gate = new();
    private long _chatInput;
    private long _chatCached;
    private long _chatOutput;
    private long _smallInput;
    private long _smallCached;
    private long _smallOutput;
    private long _embeddingTokens;
    private int _embeddingCalls;
    private int _indexQueries;
    private int _rankerQueries;
    private bool _chatReported;

    /// <summary>False until some round trip reported usage — keeps token fields null, not zero.</summary>
    public bool ChatReported { get { lock (_gate) return _chatReported; } }

    /// <summary>
    /// One round trip's usage on the **main** chat deployment, each count
    /// optional — the same permissiveness the agentic loop's old locals had:
    /// input and output are summed independently, so a report carrying only
    /// one of them still contributes its half.
    /// </summary>
    public void Chat(long? input, long? cached, long? output) => ChatInto(
        ref _chatInput, ref _chatCached, ref _chatOutput, input, cached, output);

    /// <summary>
    /// One call's usage on the **small** chat deployment — v1's guardrail,
    /// rewriter, claims audit and conversational reply. Cached input is a
    /// subset of input on the same call, priced at the small trio's rate.
    /// </summary>
    public void ChatSmall(long? input, long? cached, long? output) => ChatInto(
        ref _smallInput, ref _smallCached, ref _smallOutput, input, cached, output);

    private void ChatInto(
        ref long inputAcc, ref long cachedAcc, ref long outputAcc,
        long? input, long? cached, long? output)
    {
        lock (_gate)
        {
            if (input is { } i) { inputAcc += i; _chatReported = true; }
            if (cached is { } c) cachedAcc += c;
            if (output is { } o) { outputAcc += o; _chatReported = true; }
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
        long chatInput, chatCached, chatOutput, smallInput, smallCached, smallOutput, embeddingTokens;
        int embeddingCalls, indexQueries, rankerQueries;
        lock (_gate)
        {
            chatInput = _chatInput;
            chatCached = _chatCached;
            chatOutput = _chatOutput;
            smallInput = _smallInput;
            smallCached = _smallCached;
            smallOutput = _smallOutput;
            embeddingTokens = _embeddingTokens;
            embeddingCalls = _embeddingCalls;
            indexQueries = _indexQueries;
            rankerQueries = _rankerQueries;
        }

        // Cached is a subset of input on the same call; clamped rather than
        // trusted, so a provider reporting an inconsistent pair cannot mint
        // negative tokens into the money.
        long uncached = Math.Max(0, chatInput - chatCached);
        long smallUncached = Math.Max(0, smallInput - smallCached);
        return new TurnCost
        {
            Currency = sheet.Currency,
            PriceSheet = sheet.Id,
            ChatInputTokens = chatInput,
            ChatCachedInputTokens = chatCached,
            ChatOutputTokens = chatOutput,
            SmallChatInputTokens = smallInput,
            SmallChatCachedInputTokens = smallCached,
            SmallChatOutputTokens = smallOutput,
            EmbeddingCalls = embeddingCalls,
            EmbeddingTokens = embeddingTokens,
            IndexQueries = indexQueries,
            RankerQueries = rankerQueries,
            ChatUsd =
                uncached * sheet.ChatInputPerMillion / 1_000_000m
                + chatCached * sheet.ChatCachedInputPerMillion / 1_000_000m
                + chatOutput * sheet.ChatOutputPerMillion / 1_000_000m,
            SmallChatUsd =
                smallUncached * sheet.SmallChatInputPerMillion / 1_000_000m
                + smallCached * sheet.SmallChatCachedInputPerMillion / 1_000_000m
                + smallOutput * sheet.SmallChatOutputPerMillion / 1_000_000m,
            EmbeddingUsd = embeddingTokens * sheet.EmbeddingPerMillion / 1_000_000m,
            SearchUsd = indexQueries * sheet.SearchPerThousand / 1_000m,
        };
    }
}
