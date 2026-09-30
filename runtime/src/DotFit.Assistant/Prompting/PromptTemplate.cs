using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DotFit.Assistant.Aliases;
using DotFit.Assistant.Sources;

namespace DotFit.Assistant.Prompting;

/// <summary>
/// The system prompt as files people edit: <c>assistant/prompt/</c> holds one
/// markdown file per part (<c>posture.md</c>, <c>safety.md</c>, …), and
/// <c>variants/&lt;name&gt;.md</c> is a template that assembles them.
///
/// A template or a part writes <c>{{name}}</c> to include either another part
/// (<c>{{safety}}</c> is <c>safety.md</c>) or a block generated here:
/// <c>{{currency-facts}}</c> from the alias table, and <c>{{support-route}}</c>
/// / <c>{{support-team}}</c> from the configured support contact. Includes
/// nest. <c>&lt;!-- comments --&gt;</c> are stripped, so the reasoning behind
/// a part lives in the file beside the text it explains.
///
/// Everything that can be checked without a model is checked at load, so a
/// broken variant stops the boot instead of reaching a customer: every
/// include resolves, nothing includes itself, no part shadows a generated
/// block, and every <see cref="RequiredParts"/> entry is reachable from the
/// variant. The files are read from disk, not embedded: editing a prompt
/// needs a restart, not a rebuild, and <see cref="Version"/> in the turn log
/// says which text a turn ran on.
/// </summary>
public sealed partial class PromptTemplate
{
    public const string DefaultVariant = "default";
    public const string VariantsFolder = "variants";

    public const string CurrencyFacts = "currency-facts";
    public const string SupportRoute = "support-route";
    public const string SupportTeam = "support-team";
    public const string SourceList = "source-list";
    public const string AuthorityByDomain = "authority-by-domain";
    public const string ReferenceLibrary = "reference-library";

    /// <summary>Blocks built in code rather than read from a file.</summary>
    public static readonly IReadOnlyList<string> GeneratedBlocks =
        [CurrencyFacts, SupportRoute, SupportTeam, SourceList, AuthorityByDomain, ReferenceLibrary];

    /// <summary>Parts every variant must include, however it is arranged.</summary>
    public static readonly IReadOnlyList<string> RequiredParts = ["escalation-list"];

    private const int MaxDepth = 8;

    private readonly string _template;
    private readonly IReadOnlyDictionary<string, string> _parts;

    private PromptTemplate(string variant, string template, IReadOnlyDictionary<string, string> parts)
    {
        Variant = variant;
        _template = template;
        _parts = parts;
    }

    /// <summary>The variant this template was loaded from.</summary>
    public string Variant { get; }

    /// <summary>Names of the variants in a prompt folder, sorted.</summary>
    public static IReadOnlyList<string> Variants(string promptDir) =>
        [.. Directory.EnumerateFiles(Path.Combine(promptDir, VariantsFolder), "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)];

    public static PromptTemplate Load(string promptDir, string variant = DefaultVariant)
    {
        if (!NamePattern().IsMatch(variant))
            throw new PromptException($"prompt variant '{variant}' is not a valid name (lowercase letters, digits, dashes)");
        string variantPath = Path.Combine(promptDir, VariantsFolder, variant + ".md");
        if (!File.Exists(variantPath))
            throw new PromptException(
                $"prompt variant '{variant}' not found at {variantPath}; available: " +
                string.Join(", ", Directory.Exists(Path.Combine(promptDir, VariantsFolder)) ? Variants(promptDir) : []));

        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(promptDir, "*.md"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (GeneratedBlocks.Contains(name))
                throw new PromptException($"{file} has the name of a generated block; rename the file");
            parts[name] = Clean(File.ReadAllText(file));
        }

        var template = new PromptTemplate(variant, Clean(File.ReadAllText(variantPath)), parts);
        template.Validate();
        return template;
    }

    /// <summary>
    /// The assembled prompt. The source blocks need <paramref name="registry"/>;
    /// a variant that uses one without it fails here rather than rendering a gap.
    /// </summary>
    public string Render(AliasTable aliases, string? supportContact, SourceRegistry? registry = null) =>
        Expand(_template, name => name switch
        {
            CurrencyFacts => SystemPrompt.CurrencyFacts(aliases),
            SupportRoute => SystemPrompt.SupportRoute(supportContact),
            SupportTeam => SystemPrompt.SupportTeam(supportContact),
            SourceList => SystemPrompt.SourceList(RequireRegistry(registry, name)),
            AuthorityByDomain => SystemPrompt.AuthorityByDomain(RequireRegistry(registry, name)),
            ReferenceLibrary => SystemPrompt.ReferenceLibrary(RequireRegistry(registry, name)),
            _ => _parts[name],
        }, depth: 0, trail: []);

    private static SourceRegistry RequireRegistry(SourceRegistry? registry, string block) =>
        registry ?? throw new PromptException($"{{{{{block}}}}} needs the source registry");

    /// <summary>First 12 hex characters of the text's SHA-256, the same form the program guide's version takes.</summary>
    public static string Version(string renderedPrompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(renderedPrompt)))[..12];

    private void Validate()
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        Expand(_template, name =>
        {
            reached.Add(name);
            if (GeneratedBlocks.Contains(name))
                return "";
            return _parts.TryGetValue(name, out string? text)
                ? text
                : throw new PromptException(
                    $"prompt variant '{Variant}' includes {{{{{name}}}}}, which is neither a part " +
                    $"({name}.md) nor a generated block ({string.Join(", ", GeneratedBlocks)})");
        }, depth: 0, trail: []);

        foreach (string required in RequiredParts)
            if (!reached.Contains(required))
                throw new PromptException(
                    $"prompt variant '{Variant}' does not include {{{{{required}}}}}; every variant must");
    }

    private static string Expand(string text, Func<string, string> resolve, int depth, List<string> trail) =>
        IncludePattern().Replace(text, match =>
        {
            string name = match.Groups[1].Value;
            if (trail.Contains(name) || depth >= MaxDepth)
                throw new PromptException(
                    $"prompt includes loop: {string.Join(" -> ", trail.Append(name))}");
            string resolved = resolve(name);
            return GeneratedBlocks.Contains(name)
                ? resolved
                : Expand(resolved, resolve, depth + 1, [.. trail, name]);
        });

    /// <summary>
    /// Comments out, line endings normalized, surrounding blank lines trimmed.
    /// A comment on lines of its own takes its line break with it, so removing
    /// one never leaves a blank line where it stood.
    /// </summary>
    internal static string Clean(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = WholeLineComment().Replace(text, "");
        text = InlineComment().Replace(text, "");
        return text.Trim('\n');
    }

    [GeneratedRegex(@"\{\{\s*([a-z0-9-]+)\s*\}\}")]
    private static partial Regex IncludePattern();

    [GeneratedRegex(@"^[ \t]*<!--(?:(?!-->).)*-->[ \t]*\n", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex WholeLineComment();

    [GeneratedRegex(@"<!--(?:(?!-->).)*-->", RegexOptions.Singleline)]
    private static partial Regex InlineComment();

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex NamePattern();
}

/// <summary>A prompt folder or variant that cannot be assembled. The message names the file or include at fault.</summary>
public sealed class PromptException(string message) : Exception(message);

/// <summary>
/// The system prompt a process runs on: assembled once at boot, and named by
/// variant and version wherever a turn is recorded.
/// </summary>
public sealed record AssembledPrompt(string Variant, string Text)
{
    public string Version { get; } = PromptTemplate.Version(Text);

    public static AssembledPrompt Load(
        Config.RuntimeOptions options, Config.AssistantOptions assistant, AliasTable aliases, SourceRegistry registry) =>
        new(assistant.PromptVariant,
            PromptTemplate.Load(options.PromptDir, assistant.PromptVariant)
                .Render(aliases, options.SupportContact, registry));
}
