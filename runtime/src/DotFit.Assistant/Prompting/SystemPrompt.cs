using System.Text;
using DotFit.Assistant.Aliases;
using DotFit.Assistant.Sources;
using DotFit.Assistant.Tools;

namespace DotFit.Assistant.Prompting;

/// <summary>
/// The parts of the system prompt that are generated rather than written:
/// product-currency facts from the alias table and the support route from
/// configuration. The written parts live in <c>assistant/prompt/</c> and are
/// assembled by <see cref="PromptTemplate"/>. Also the two fixed texts the
/// caller shows without a model call: the AI disclosure and the handoff.
/// </summary>
public static class SystemPrompt
{
    /// <summary>
    /// The support route the prompt and the handoff give out when configuration
    /// does not override it: the pair dotFIT itself publishes.
    /// </summary>
    public const string DefaultSupportContact = "support@dotfit.com or (877) 436-8348";

    /// <summary>
    /// Product currency, rendered from the alias table: what lets "what happened
    /// to LeanMR?" be answered without a search. The rename/replacement
    /// distinction is the point of rendering it: a rename is one product under
    /// two names; a replacement is a different formula, and conflating them is a
    /// product claim about something that does not exist. Ordered so the prompt
    /// is identical across boots.
    /// </summary>
    public static string CurrencyFacts(AliasTable aliases)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Product names change. These are the current facts — they override anything a");
        sb.AppendLine("source says about a name, and you can use them without searching:");
        sb.AppendLine();

        if (aliases.LegacyRenames.Count > 0)
        {
            sb.AppendLine("Renamed — same product, same formula; use the current name, and mention the old one only when the customer used it:");
            foreach (LegacyRename rename in aliases.LegacyRenames.OrderBy(r => r.Deprecated, StringComparer.Ordinal))
                sb.Append("- ").Append(rename.Deprecated).Append(" is now ").Append(rename.CurrentFamily).AppendLine();
            sb.AppendLine();
        }

        if (aliases.Replacements.Count > 0)
        {
            sb.AppendLine("Replaced — a DIFFERENT formula, never the same product under a new name:");
            foreach (Replacement replacement in aliases.Replacements.OrderBy(r => r.Deprecated, StringComparer.Ordinal))
            {
                sb.Append("- ").Append(replacement.Deprecated)
                  .Append(" was discontinued and replaced by ").Append(replacement.SuccessorFamily)
                  .AppendLine(". They are not the same product; do not carry a claim from one to the other.");
            }
            sb.AppendLine();
        }

        if (aliases.DiscontinuedProducts.Count > 0)
        {
            sb.AppendLine("Discontinued:");
            foreach (Discontinued discontinued in aliases.DiscontinuedProducts.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                sb.Append("- ").Append(discontinued.Name);
                if (!string.IsNullOrWhiteSpace(discontinued.Note))
                    sb.Append(" — ").Append(discontinued.Note);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        sb.Append("Current product families: ").AppendLine(string.Join(", ",
            aliases.Families.Select(f => f.Family).OrderBy(f => f, StringComparer.Ordinal)));
        // AppendLine writes the platform newline; the prompt uses LF everywhere.
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    /// <summary>
    /// The <c>{{source-list}}</c> block: each searchable corpus, what it is,
    /// and the claims tag its sources carry.
    /// </summary>
    public static string SourceList(SourceRegistry registry) => string.Join("\n",
        registry.Corpora.Select(c => $"- {Capitalize(c.Name)}: {c.About}. {SourceLabels.ClaimsMarker(c.Authority)}."));

    /// <summary>The <c>{{authority-by-domain}}</c> block: per topic, which source wins a disagreement.</summary>
    public static string AuthorityByDomain(SourceRegistry registry) => string.Join("\n",
        registry.Authority.Select(a =>
            $"- {Capitalize(a.Topic)}: {string.Join(", then ", a.Ids.Select(registry.DisplayName))}."));

    /// <summary>
    /// The <c>{{reference-library}}</c> block: what read_reference can open,
    /// when to open it, and its sections, so the model can read one section
    /// instead of the whole document.
    /// </summary>
    public static string ReferenceLibrary(SourceRegistry registry) => string.Join("\n",
        registry.References.Select(r =>
            $"- {r.Id}: {r.Title}. Read it for {r.UseWhen}." +
            (r.PolicyFigures ? " Its prices, fees and thresholds are fixed dotFIT policy and may be stated." : "") +
            (r.Sections.Count > 0 ? $" Sections: {string.Join("; ", r.Sections)}." : "")));

    private static string Capitalize(string text) =>
        text.Length == 0 || text.StartsWith("dotFIT", StringComparison.Ordinal)
            ? text
            : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The <c>{{support-route}}</c> block: where the model routes someone who needs a person.</summary>
    public static string SupportRoute(string? supportContact) => supportContact is null
        ? "offer the dotFIT support team or their own healthcare professional."
        : $"dotFIT support at {supportContact}, or their own healthcare professional.";

    /// <summary>The <c>{{support-team}}</c> block: how the model names support for a support request.</summary>
    public static string SupportTeam(string? supportContact) => supportContact is null
        ? "dotFIT support"
        : $"dotFIT support ({supportContact})";

    /// <summary>What the caller renders before the first answer of a conversation.</summary>
    public static string ConversationDisclosure() =>
        "I'm an AI assistant — nutrition guidance, not medical advice. " +
        "I answer from dotFIT's website pages and approved product copy, the " +
        "practitioner reference guide, customer Q&A and podcasts, and I cite " +
        "my sources. " +
        "For anything medical, I'll hand you to the dotFIT support team or a " +
        "healthcare professional.";

    /// <summary>
    /// What the customer gets when the turn threw. Templated, not generated:
    /// the answer to a failed model call must not be another model call that
    /// can fail the same way.
    /// </summary>
    public static string HandoffMessage(string? supportContact = DefaultSupportContact) =>
        supportContact is null
            ? "Something went wrong on my end and I couldn't get you an answer. Please try again, or reach " +
              "out to the dotFIT support team."
            : $"Something went wrong on my end and I couldn't get you an answer. Please try again, or reach " +
              $"the dotFIT support team at {supportContact}.";
}
