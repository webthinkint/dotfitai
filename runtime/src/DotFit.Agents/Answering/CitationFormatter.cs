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
    /// <summary>
    /// One bracketed marker, which may carry a group: <c>[1]</c>, <c>[1, 2]</c>,
    /// <c>[1;2]</c>, <c>[1-3]</c>. Models reach for the grouped forms unprompted,
    /// and a marker the parser cannot see is a marker the post-check counts as
    /// missing — which failed whole, well-grounded answers on citation_presence.
    /// Three digits max, so a bracketed year range is not mistaken for a group.
    /// </summary>
    [GeneratedRegex(@"\[\s*(\d{1,3}(?:\s*[,;–—-]\s*\d{1,3})*)\s*\]")]
    private static partial Regex MarkerRegex();

    private static readonly char[] RangeSeparators = ['-', '–', '—'];

    /// <summary>Widest <c>[1-3]</c> span expanded rather than read as two endpoints.</summary>
    private const int MaxRangeSpan = 20;

    /// <summary>Distinct marker numbers in order of first appearance.</summary>
    public static IReadOnlyList<int> ExtractMarkers(string answer)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();
        foreach (Match m in MarkerRegex().Matches(answer))
        {
            foreach (int n in ExpandGroup(m.Groups[1].Value))
            {
                if (seen.Add(n))
                    ordered.Add(n);
            }
        }
        return ordered;
    }

    /// <summary>The numbers inside one bracket, lists and ranges flattened.</summary>
    private static IEnumerable<int> ExpandGroup(string group)
    {
        foreach (string part in group.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            int sep = p.IndexOfAny(RangeSeparators);
            if (sep > 0
                && int.TryParse(p[..sep].Trim(), out int lo)
                && int.TryParse(p[(sep + 1)..].Trim(), out int hi))
            {
                if (hi >= lo && hi - lo <= MaxRangeSpan)
                {
                    for (int n = lo; n <= hi; n++)
                        yield return n;
                }
                else
                {
                    // Descending or implausibly wide: keep the endpoints, which
                    // the out-of-range check can still flag.
                    yield return lo;
                    yield return hi;
                }
            }
            else if (int.TryParse(p, out int single))
            {
                yield return single;
            }
        }
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
