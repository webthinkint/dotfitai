using System.Runtime.CompilerServices;
using System.Text.Json;
using DotFit.Agentic.Retrieval;
using DotFit.Agents.Aliases;
using DotFit.Agents.Retrieval;
using Microsoft.Extensions.AI;

namespace DotFit.Agentic.Tests;

/// <summary>
/// An <see cref="IChatClient"/> that replays a script (design §11: "script the
/// IChatClient for the loop").
///
/// One scripted turn per model round trip. The Agent Framework's
/// function-invoking decorator sits between this fake and the agent, so a
/// script that emits a <see cref="FunctionCallContent"/> really does invoke the
/// real tool and really does come back for the next scripted turn — which is
/// what makes these loop tests worth having rather than a mock of a mock.
/// </summary>
internal sealed class ScriptedChatClient(params IEnumerable<IList<AIContent>> turns) : IChatClient
{
    private readonly Queue<IList<AIContent>> _turns = new(turns);

    /// <summary>Every message list the fake was called with, for asserting on what the model saw.</summary>
    public List<List<ChatMessage>> Calls { get; } = [];

    /// <summary>Thrown instead of answering, when set — the failure path (§9).</summary>
    public Exception? Throw { get; set; }

    public static IList<AIContent> Text(string text) => [new TextContent(text)];

    public static IList<AIContent> Call(string callId, string name, object arguments) =>
    [
        new FunctionCallContent(callId, name,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))),
    ];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add([.. messages]);
        if (Throw is not null)
            throw Throw;

        IList<AIContent> turn = _turns.Count > 0 ? _turns.Dequeue() : [new TextContent("")];
        foreach (AIContent content in turn)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add([.. messages]);
        if (Throw is not null)
            throw Throw;
        IList<AIContent> turn = _turns.Count > 0 ? _turns.Dequeue() : [new TextContent("")];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, turn)));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Search that returns a fixed list, recording what it was asked for.</summary>
internal sealed class FakeSearch(params RetrievedDocument[] documents) : IKnowledgeSearch
{
    public List<SearchParameters> Queries { get; } = [];
    public Func<SearchParameters, IReadOnlyList<RetrievedDocument>>? Handler { get; set; }

    public Task<IReadOnlyList<RetrievedDocument>> SearchAsync(
        SearchParameters parameters, CancellationToken ct = default)
    {
        Queries.Add(parameters);
        return Task.FromResult(Handler?.Invoke(parameters) ?? (IReadOnlyList<RetrievedDocument>)documents);
    }
}

internal sealed class FakeDocumentStore(params RetrievedDocument[] documents) : IDocumentStore
{
    private readonly Dictionary<string, RetrievedDocument> _byId =
        documents.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public List<string> Filters { get; } = [];

    public Task<RetrievedDocument?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out RetrievedDocument? d) ? d : null);

    public Task<IReadOnlyList<RetrievedDocument>> FilterAsync(
        string filter, int top, CancellationToken ct = default)
    {
        Filters.Add(filter);
        return Task.FromResult<IReadOnlyList<RetrievedDocument>>(
            [.. documents.Where(d => filter.Contains($"'{d.SourceType}'", StringComparison.Ordinal)
                                     || d.Products.Any(p => filter.Contains($"'{p}'", StringComparison.Ordinal)))]);
    }
}

/// <summary>
/// Synthetic fixtures only. Real corpus text never appears in tests — the §4
/// PII posture predates this branch and is not relaxed by it.
/// </summary>
internal static class Fixtures
{
    public static RetrievedDocument Document(
        string id = "pdsrg-example-001",
        string sourceType = "pdsrg",
        int authority = 2,
        string title = "Example Section",
        string content = "Example body text.",
        string[]? products = null,
        string? locator = null,
        string? productStatus = null) => new()
        {
            Id = id,
            SourceType = sourceType,
            Authority = authority,
            Title = title,
            Content = content,
            Products = products ?? [],
            Locator = locator,
            ProductStatus = productStatus,
            IsCurrent = true,
            CitationUrl = "https://example.com/doc",
        };

    /// <summary>
    /// A minimal alias table with one family, one rename, one replacement and
    /// one discontinued SKU — the four shapes §6's currency facts must keep
    /// apart. Invented names, so nothing here asserts on real curation.
    /// </summary>
    public const string AliasJson = """
        {
          "version": "test-1",
          "families": [
            {"family": "ExampleFormula", "canonical_part_no": 9001, "part_nos": [9001, 9002]},
            {"family": "OtherFormula", "canonical_part_no": 9100, "part_nos": [9100]}
          ],
          "deterministic_aliases": [
            {"token": "EF", "family": "ExampleFormula", "part_nos": [9001, 9002]}
          ],
          "llm_only_aliases": [
            {"token": "the example one", "family": "ExampleFormula", "part_nos": [9001]}
          ],
          "context_only_tokens": {"XY": "could mean two different things"},
          "legacy_renames": [
            {"deprecated": "OldExample", "current_family": "ExampleFormula", "part_nos": [9001]}
          ],
          "replacements": [
            {"deprecated": "RetiredFormula", "successor_family": "OtherFormula",
             "successor_part_nos": [9100], "note": "different formula"}
          ],
          "discontinued": [
            {"name": "GoneFormula", "note": "no longer made"}
          ]
        }
        """;

    public static AliasTable Aliases() => AliasTable.FromJson(AliasJson);
}
