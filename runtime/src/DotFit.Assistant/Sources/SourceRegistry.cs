using System.Text.RegularExpressions;
using DotFit.Assistant.Prompting;
using DotFit.Assistant.Tools;
using YamlDotNet.RepresentationModel;

namespace DotFit.Assistant.Sources;

/// <summary>
/// <c>assistant/sources.yaml</c>: every source the assistant can read and what
/// each is for. The system prompt's source list, authority order and reference
/// library, and <c>read_reference</c>'s list, are generated from it.
///
/// Validated at load, so a broken entry stops the boot: the corpora must be
/// exactly the index's source types, every reference file must exist and have
/// text, ids are unique across corpora and references, and every id in the
/// authority order is one of them.
/// </summary>
public sealed partial class SourceRegistry
{
    public const string FileName = "sources.yaml";

    private SourceRegistry(
        IReadOnlyList<Corpus> corpora, IReadOnlyList<Reference> references, IReadOnlyList<AuthorityOrder> authority)
    {
        Corpora = corpora;
        References = references;
        Authority = authority;
    }

    public IReadOnlyList<Corpus> Corpora { get; }
    public IReadOnlyList<Reference> References { get; }
    public IReadOnlyList<AuthorityOrder> Authority { get; }

    public Reference? FindReference(string id) =>
        References.FirstOrDefault(r => string.Equals(r.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A corpus's name or a reference's title, for text the model reads.</summary>
    public string DisplayName(string id) =>
        Corpora.FirstOrDefault(c => c.Id == id)?.Name
        ?? References.FirstOrDefault(r => r.Id == id)?.Title
        ?? id;

    public static SourceRegistry Load(string path)
    {
        if (!File.Exists(path))
            throw new SourceRegistryException($"source registry not found at {path}");
        return Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", path);
    }

    /// <summary>Parse registry YAML; reference files resolve against <paramref name="baseDir"/>.</summary>
    public static SourceRegistry Parse(string yaml, string baseDir, string origin = FileName)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new SourceRegistryException($"{origin}: not valid YAML ({e.Message})");
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new SourceRegistryException($"{origin}: expected a mapping with corpora, references and authority");

        var corpora = new List<Corpus>();
        foreach ((string id, YamlMappingNode node) in Entries(root, "corpora", origin))
            corpora.Add(new Corpus(id, Required(node, "name", id, origin), Required(node, "about", id, origin)));

        var expected = SourceLabels.SourceTypeAuthority.Keys.ToHashSet(StringComparer.Ordinal);
        var listed = corpora.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(listed))
            throw new SourceRegistryException(
                $"{origin}: corpora must be exactly the index's source types ({string.Join(", ", expected)}); " +
                $"missing: {string.Join(", ", expected.Except(listed))}; unknown: {string.Join(", ", listed.Except(expected))}");

        var references = new List<Reference>();
        foreach ((string id, YamlMappingNode node) in Entries(root, "references", origin))
        {
            if (!IdPattern().IsMatch(id))
                throw new SourceRegistryException($"{origin}: reference id '{id}' must be lowercase letters, digits and dashes");
            string file = Required(node, "file", id, origin);
            string path = Path.GetFullPath(Path.Combine(baseDir, file));
            if (!File.Exists(path))
                throw new SourceRegistryException($"{origin}: reference '{id}' file not found: {path}");
            string text = PromptTemplate.Clean(File.ReadAllText(path));
            if (text.Length == 0)
                throw new SourceRegistryException($"{origin}: reference '{id}' file is empty: {path}");
            references.Add(new Reference(
                id,
                Required(node, "title", id, origin),
                Required(node, "use_when", id, origin),
                Optional(node, "policy_figures") is { } flag && bool.TryParse(flag, out bool policy) && policy,
                text));
        }

        var ids = corpora.Select(c => c.Id).Concat(references.Select(r => r.Id)).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new SourceRegistryException($"{origin}: ids must be unique across corpora and references");

        var authority = new List<AuthorityOrder>();
        if (root.Children.TryGetValue(new YamlScalarNode("authority"), out YamlNode? authorityNode))
        {
            if (authorityNode is not YamlMappingNode topics)
                throw new SourceRegistryException($"{origin}: authority must map topics to lists of ids");
            foreach ((YamlNode key, YamlNode value) in topics.Children)
            {
                string topic = ((YamlScalarNode)key).Value ?? "";
                if (value is not YamlSequenceNode order)
                    throw new SourceRegistryException($"{origin}: authority '{topic}' must be a list of ids");
                string[] members = [.. order.Children.Select(n => ((YamlScalarNode)n).Value ?? "")];
                foreach (string member in members)
                    if (!ids.Contains(member))
                        throw new SourceRegistryException($"{origin}: authority '{topic}' names unknown id '{member}'");
                authority.Add(new AuthorityOrder(topic, members));
            }
        }

        return new SourceRegistry(corpora, references, authority);
    }

    private static IEnumerable<(string Id, YamlMappingNode Node)> Entries(YamlMappingNode root, string key, string origin)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node))
            yield break;
        if (node is not YamlMappingNode map)
            throw new SourceRegistryException($"{origin}: {key} must map ids to entries");
        foreach ((YamlNode k, YamlNode v) in map.Children)
        {
            string id = ((YamlScalarNode)k).Value ?? "";
            if (v is not YamlMappingNode entry)
                throw new SourceRegistryException($"{origin}: {key}.{id} must be a mapping");
            yield return (id, entry);
        }
    }

    private static string? Optional(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? v) && v is YamlScalarNode s
            && !string.IsNullOrWhiteSpace(s.Value)
            ? s.Value.Trim()
            : null;

    private static string Required(YamlMappingNode node, string key, string id, string origin) =>
        Optional(node, key) ?? throw new SourceRegistryException($"{origin}: {id} needs a '{key}'");

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex IdPattern();
}

/// <summary>A searchable corpus: an index <c>source_type</c>, named for the model.</summary>
public sealed record Corpus(string Id, string Name, string About)
{
    public int Authority => SourceLabels.SourceTypeAuthority[Id];
}

/// <summary>One topic's authority order, most authoritative first.</summary>
public sealed record AuthorityOrder(string Topic, IReadOnlyList<string> Ids);

/// <summary>
/// A reference document: dotFIT's own guidance, read by <c>read_reference</c>,
/// never cited. Its version is the hash of its text, logged whenever a turn
/// reads it, because an uncited source is otherwise invisible in the record.
/// </summary>
public sealed partial class Reference
{
    public Reference(string id, string title, string useWhen, bool policyFigures, string text)
    {
        Id = id;
        Title = title;
        UseWhen = useWhen;
        PolicyFigures = policyFigures;
        Text = text;
        Version = PromptTemplate.Version(text);
        Headings = [.. HeadingPattern().Matches(text).Select(m => new Heading(
            m.Groups[1].Value.Length, Tidy(m.Groups[2].Value), m.Index))];
    }

    public string Id { get; }
    public string Title { get; }
    public string UseWhen { get; }
    public bool PolicyFigures { get; }
    public string Text { get; }
    public string Version { get; }
    public IReadOnlyList<Heading> Headings { get; }

    /// <summary>
    /// The headings the library lists: the shallowest level that occurs more
    /// than once, so a document titled by one top heading lists its chapters.
    /// </summary>
    public IReadOnlyList<string> Sections
    {
        get
        {
            int? level = Headings.GroupBy(h => h.Level).Where(g => g.Count() > 1).Select(g => (int?)g.Key).Min();
            return level is null ? [] : [.. Headings.Where(h => h.Level == level).Select(h => h.Text)];
        }
    }

    /// <summary>
    /// The section under the heading that matches: exact text first, then the
    /// first heading containing it, both case-insensitive. The section runs to
    /// the next heading at the same or a higher level. Null when nothing matches.
    /// </summary>
    public string? Section(string query)
    {
        string q = query.Trim();
        Heading? found = Headings.FirstOrDefault(h => string.Equals(h.Text, q, StringComparison.OrdinalIgnoreCase))
            ?? Headings.FirstOrDefault(h => h.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (found is null || q.Length == 0)
            return null;
        Heading? next = Headings.FirstOrDefault(h => h.Offset > found.Offset && h.Level <= found.Level);
        return Text[found.Offset..(next?.Offset ?? Text.Length)].TrimEnd();
    }

    /// <summary>Heading text as a reader should see it: slide citations and stray emphasis removed.</summary>
    private static string Tidy(string heading) =>
        SlideNote().Replace(heading, "").Replace("**", "", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\s*\((?:slide|slides)\b[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SlideNote();
}

public sealed record Heading(int Level, string Text, int Offset);

/// <summary>A registry that cannot be loaded. The message names the file and the entry at fault.</summary>
public sealed class SourceRegistryException(string message) : Exception(message);
