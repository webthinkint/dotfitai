using System.Text.Json.Serialization;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using DotFit.Agents.Retrieval;

namespace DotFit.Agentic.Retrieval;

/// <summary>
/// The two index reads that are not a hybrid query (design §7.2, §7.3):
/// fetch one document by key, and pull every document matching an OData
/// filter. <c>DotFit.Agents</c>'s <see cref="IKnowledgeSearch"/> covers the
/// third — searching — and is used as-is.
///
/// Why this is a separate interface rather than a widened one: v1's search
/// client is the comparison baseline and stays untouched (design §4). The
/// POCO below duplicates its <c>KbDocument</c>, which is internal to that
/// assembly; twelve fields of duplication is cheaper than making one
/// assembly's internals another's public contract. The §9 schema is the thing
/// both mirror, and a test pins the field names on this side.
/// </summary>
public interface IDocumentStore
{
    /// <summary>One document by key. Null when the key does not exist.</summary>
    Task<RetrievedDocument?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Every document matching <paramref name="filter"/>, ordered by id so the
    /// result is stable across calls. The <c>is_current</c> contract is ANDed
    /// in by the implementation, never by the caller.
    /// </summary>
    Task<IReadOnlyList<RetrievedDocument>> FilterAsync(
        string filter, int top, CancellationToken ct = default);
}

public sealed class AzureDocumentStore(SearchClient search, SearchSettings? settings = null) : IDocumentStore
{
    private readonly SearchSettings _settings = settings ?? new SearchSettings();

    public async Task<RetrievedDocument?> GetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            Response<KbDoc> response = await search.GetDocumentAsync<KbDoc>(id, cancellationToken: ct)
                .ConfigureAwait(false);
            return ToDocument(response.Value);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // A miss is an ordinary answer here — the model invents ids the way
            // it invents anything else, and the tool tells it so in prose.
            return null;
        }
    }

    public async Task<IReadOnlyList<RetrievedDocument>> FilterAsync(
        string filter, int top, CancellationToken ct = default)
    {
        var options = new SearchOptions
        {
            // Same hard contract as the search path: AI Search does not match a
            // filter against null, so an unstamped document is invisible — and
            // a caller filter with a top-level `or` must not widen past it,
            // hence the parentheses.
            Filter = $"{_settings.CurrentFilter} and ({filter})",
            Size = Math.Max(1, top),
            OrderBy = { "id asc" },
        };
        Response<SearchResults<KbDoc>> response =
            await search.SearchAsync<KbDoc>(searchText: "*", options, ct).ConfigureAwait(false);

        var docs = new List<RetrievedDocument>();
        await foreach (SearchResult<KbDoc> result in response.Value.GetResultsAsync().WithCancellation(ct))
            docs.Add(ToDocument(result.Document));
        return docs;
    }

    private static RetrievedDocument ToDocument(KbDoc d) => new()
    {
        Id = d.Id ?? "",
        SourceType = d.SourceType ?? "",
        Authority = d.Authority,
        Title = d.Title ?? "",
        Content = d.Content ?? "",
        CitationUrl = d.CitationUrl,
        Locator = d.Locator,
        Products = d.Products ?? [],
        Topics = d.Topics ?? [],
        Date = d.Date,
        IsCurrent = d.IsCurrent,
        ProductStatus = d.ProductStatus,
        // No retrieval score on either path: nothing here was ranked. Leaving
        // Score at 0 is honest; a fabricated one would sort wrong if these
        // documents ever met search hits in the same list.
    };

    /// <summary>
    /// The §9 fields, with explicit JSON names so the mapping holds whatever
    /// the serializer's naming policy is (the classic null-properties gotcha).
    /// The vector field is <c>stored=false</c> and never comes back.
    /// </summary>
    internal sealed class KbDoc
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

/// <summary>
/// OData filters the tools build, in one place so they are testable without an
/// Azure client and so quoting is not re-derived per call site.
/// </summary>
public static class Filters
{
    /// <summary>
    /// OData string literals escape a single quote by doubling it. Every value
    /// reaching a filter came from the model, which is untrusted input by
    /// construction — the arguments of a tool call are chosen by the model, not
    /// by the customer, but neither is a reason to concatenate raw text into a
    /// query.
    /// </summary>
    public static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    public static string SourceType(string sourceType) => $"source_type eq {Quote(sourceType)}";

    /// <summary>Any of the given part numbers tags the document.</summary>
    public static string AnyProduct(IEnumerable<string> partNos)
    {
        string[] values = [.. partNos.Select(Quote)];
        return values.Length == 0
            ? ""
            : $"products/any(p: p eq {string.Join(" or p eq ", values)})";
    }

    /// <summary>Every non-empty clause ANDed, each parenthesized.</summary>
    public static string And(params string?[] clauses)
    {
        string[] kept = [.. clauses.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => $"({c!.Trim()})")];
        return kept.Length == 0 ? "" : string.Join(" and ", kept);
    }
}
