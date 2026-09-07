using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using System.Text.Json.Serialization;
using OpenAI;
using OpenAI.Embeddings;

namespace DotFit.Agents.Retrieval;

/// <summary>
/// Hybrid BM25 + vector search over <c>kb-main</c> (plan §9/§11): the query is
/// embedded with the same deployment the index was built with
/// (text-embedding-3-large, 3072-dim), searched with the is_current prefilter,
/// optionally re-ranked by the semantic ranker, then deterministically
/// re-ranked by authority tier.
/// </summary>
public sealed class AzureKnowledgeSearch : IKnowledgeSearch
{
    private readonly EmbeddingClient _embeddings;
    private readonly SearchClient _search;
    private readonly SearchSettings _settings;

    public AzureKnowledgeSearch(
        AzureOpenAIClient openAi, string embeddingDeployment,
        SearchClient searchClient, SearchSettings? settings = null)
    {
        _embeddings = openAi.GetEmbeddingClient(embeddingDeployment);
        _search = searchClient;
        _settings = settings ?? new SearchSettings();
    }

    public async Task<IReadOnlyList<RetrievedDocument>> SearchAsync(SearchParameters p, CancellationToken ct = default)
    {
        ReadOnlyMemory<float> vector =
            (await _embeddings.GenerateEmbeddingAsync(p.QueryText, cancellationToken: ct).ConfigureAwait(false))
            .Value.ToFloats();

        var options = new SearchOptions
        {
            // Every source must stamp is_current — AI Search does not match null
            // against a filter (plan §9), so this is a hard contract with the index.
            Filter = BuildFilter(_settings.CurrentFilter, p.AdditionalFilter),
            // retrieve a wider pool than Top so the authority re-rank has room
            Size = Math.Max(p.Top * 3, 25),
            VectorSearch = new()
            {
                Queries =
                {
                    new VectorizedQuery(vector)
                    {
                        KNearestNeighborsCount = p.VectorCandidates,
                        Fields = { "content_vector" },
                    },
                },
            },
        };
        if (p.Semantic)
        {
            options.QueryType = SearchQueryType.Semantic;
            options.SemanticSearch = new() { SemanticConfigurationName = _settings.SemanticConfiguration };
        }

        Response<SearchResults<KbDocument>> response =
            await _search.SearchAsync<KbDocument>(p.QueryText, options, ct).ConfigureAwait(false);
        var docs = new List<RetrievedDocument>();
        await foreach (SearchResult<KbDocument> result in response.Value.GetResultsAsync().WithCancellation(ct))
        {
            docs.Add(ToDocument(result));
        }
        return AuthorityBoost.Rerank(docs, _settings.AuthorityWeights, p.Top);
    }

    /// <summary>
    /// The is_current contract ANDed with the caller's extra OData filter. The
    /// extra filter is parenthesized: without it a top-level `or` would widen
    /// the query past is_current and pull superseded documents back in. Static
    /// and pure so it is testable without an Azure client.
    /// </summary>
    internal static string BuildFilter(string currentFilter, string? additional) =>
        string.IsNullOrWhiteSpace(additional)
            ? currentFilter
            : $"{currentFilter} and ({additional.Trim()})";

    private static RetrievedDocument ToDocument(SearchResult<KbDocument> r) => new()
    {
        Id = r.Document.Id ?? "",
        SourceType = r.Document.SourceType ?? "",
        Authority = r.Document.Authority,
        Title = r.Document.Title ?? "",
        Content = r.Document.Content ?? "",
        CitationUrl = r.Document.CitationUrl,
        Locator = r.Document.Locator,
        Products = r.Document.Products ?? [],
        Topics = r.Document.Topics ?? [],
        Date = r.Document.Date,
        IsCurrent = r.Document.IsCurrent,
        ProductStatus = r.Document.ProductStatus,
        Score = r.Score ?? 0,
        RerankerScore = r.SemanticSearch?.RerankerScore,
    };

    /// <summary>
    /// POCO for the §9 fields — explicit JSON names so it matches the index
    /// regardless of the serializer's naming policy (the classic
    /// null-properties gotcha). The vector field is stored=false, never returned.
    /// </summary>
    internal sealed class KbDocument
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("source_type")] public string? SourceType { get; set; }
        [JsonPropertyName("authority")] public int Authority { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("citation_url")] public string? CitationUrl { get; set; }
        [JsonPropertyName("locator")] public string? Locator { get; set; }
        [JsonPropertyName("products")] public string[]? Products { get; set; }
        [JsonPropertyName("topics")] public string[]? Topics { get; set; }
        [JsonPropertyName("date")] public DateTimeOffset? Date { get; set; }
        [JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
        [JsonPropertyName("product_status")] public string? ProductStatus { get; set; }
    }
}
