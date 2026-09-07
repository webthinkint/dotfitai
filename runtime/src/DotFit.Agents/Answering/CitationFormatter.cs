using System.Text.RegularExpressions;
using DotFit.Agents.Retrieval;

namespace DotFit.Agents.Answering;

public sealed record Citation(int Index, string SourceId, string Title, string Line);

/// <summary>
/// Maps the answer's <c>[n]</c> markers to the numbered sources that were fed
/// to the model (1-based, in prompt order). Pure and deterministic.
/// </summary>
public static partial class CitationFormatter
{
    [GeneratedRegex(@"\[(\d{1,3})\]")]
    private static partial Regex MarkerRegex();

    /// <summary>Distinct marker numbers in order of first appearance.</summary>
    public static IReadOnlyList<int> ExtractMarkers(string answer)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();
        foreach (Match m in MarkerRegex().Matches(answer))
        {
            if (int.TryParse(m.Groups[1].Value, out int n) && seen.Add(n))
                ordered.Add(n);
        }
        return ordered;
    }

    /// <summary>Marker numbers that point past the source list (a post-check warning).</summary>
    public static IReadOnlyList<int> OutOfRange(string answer, int sourceCount) =>
        ExtractMarkers(answer).Where(n => n < 1 || n > sourceCount).ToList();

    /// <summary>Citations for every in-range marker, in first-appearance order.</summary>
    public static IReadOnlyList<Citation> Collect(string answer, IReadOnlyList<RetrievedDocument> sources)
    {
        var citations = new List<Citation>();
        foreach (int n in ExtractMarkers(answer))
        {
            if (n < 1 || n > sources.Count)
                continue;
            var doc = sources[n - 1];
            string link = doc.CitationUrl ?? doc.Locator ?? "no link";
            citations.Add(new Citation(
                n, doc.Id, doc.Title,
                $"[{n}] {doc.Title} — {Prompts.SourceKind(doc.SourceType)} · {link}"));
        }
        return citations;
    }

    public static string RenderBlock(IReadOnlyList<Citation> citations) =>
        citations.Count == 0 ? "" : "Sources:\n" + string.Join("\n", citations.Select(c => c.Line));
}
