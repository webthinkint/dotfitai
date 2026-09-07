using DotFit.Agents.Answering;
using DotFit.Agents.Retrieval;
using Microsoft.Agents.AI;

namespace DotFit.Agents.Tests;

/// <summary>Scripted answer agent — records the user message it was given.</summary>
internal sealed class FakeAnswerAgent : IAnswerAgent
{
    public List<string> UserMessages { get; } = [];
    public string Reply { get; set; } = "";

    public async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        UserMessages.Add(userMessage);
        await Task.Yield();
        foreach (string chunkText in new[] { Reply[..Math.Min(10, Reply.Length)], Reply[Math.Min(10, Reply.Length)..] })
        {
            if (chunkText.Length == 0)
                continue;
            yield return new AgentResponseUpdate(null, chunkText);
        }
    }
}

/// <summary>Scripted knowledge search — records the parameters it received.</summary>
internal sealed class FakeKnowledgeSearch : IKnowledgeSearch
{
    public List<SearchParameters> Calls { get; } = [];
    public IReadOnlyList<RetrievedDocument> Results { get; set; } = [];

    public Task<IReadOnlyList<RetrievedDocument>> SearchAsync(
        SearchParameters parameters, CancellationToken ct = default)
    {
        Calls.Add(parameters);
        return Task.FromResult(Results);
    }
}

internal static class TestDocs
{
    // Synthetic fixtures only — never real corpus text (same rule as the pipeline tests).
    public static RetrievedDocument Product(string title = "Test Product", string content = "Approved copy: supports normal energy metabolism.", int authority = 1, double score = 0.02) => new()
    {
        Id = $"product-{title.ToLowerInvariant().Replace(' ', '-')}",
        SourceType = "product",
        Authority = authority,
        Title = title,
        Content = content,
        CitationUrl = "https://example.com/products/test",
        IsCurrent = true,
        Score = score,
    };

    public static RetrievedDocument Pdsrg(string title = "Test Guide Section", double score = 0.015) => new()
    {
        Id = "pdsrg-x-001",
        SourceType = "pdsrg",
        Authority = 2,
        Title = title,
        Content = "Reference guide text about the test ingredient.",
        CitationUrl = "https://example.com/guide/test.pdf#page=3",
        IsCurrent = true,
        Score = score,
    };

    public static RetrievedDocument Qa(string title = "customer question about test", double score = 0.01) => new()
    {
        Id = "qa-x-001",
        SourceType = "qa",
        Authority = 3,
        Title = title,
        Content = "Expert answer from the QA corpus.",
        IsCurrent = true,
        Date = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
        Score = score,
    };
}
