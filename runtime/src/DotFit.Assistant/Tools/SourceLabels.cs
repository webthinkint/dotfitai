namespace DotFit.Assistant.Tools;

/// <summary>
/// How a numbered source is labelled: to the model, whether it may supply
/// product-claim wording and where it comes from; to the caller, a short kind
/// for the citation line.
/// </summary>
public static class SourceLabels
{
    /// <summary>
    /// The index's source types and the authority tier the pipeline stamps on
    /// each. The index is the contract; <c>assistant/sources.yaml</c> must list
    /// exactly these corpora.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> SourceTypeAuthority = new Dictionary<string, int>
    {
        ["product"] = 1,
        ["infopage"] = 1,
        ["pdsrg"] = 2,
        ["qa"] = 3,
        ["podcast"] = 4,
        ["menu_desc"] = 5,
    };

    /// <summary>
    /// Whether a source may supply product-claim wording: approved product copy
    /// and website pages (authority 1) and the practitioner guide (authority 2).
    /// Customer Q&amp;A, podcasts and menus are context, never claim wording.
    /// </summary>
    public static bool ClaimsQuotable(int authority) => authority <= 2;

    /// <summary>
    /// The per-source tag the system prompt's claims rule keys off. Tagging every
    /// source makes the rule structural: a claim lifted from a context source
    /// and cited beside an approved one is visible as such in the context.
    /// </summary>
    public static string ClaimsMarker(int authority) => ClaimsQuotable(authority)
        ? "QUOTABLE FOR PRODUCT CLAIMS"
        : "CONTEXT ONLY";

    /// <summary>
    /// The model-facing label on each numbered source, and so what the model
    /// echoes when it attributes something in prose. It reads as provenance
    /// ("dotFIT nutrition knowledge base"), not as the name of the corpus, which
    /// would tell a customer how the material was assembled instead of where
    /// the guidance comes from. Quotability is carried by
    /// <see cref="ClaimsMarker"/>, never by this label.
    /// </summary>
    public static string SourceLabel(string sourceType, int authority) => sourceType switch
    {
        "product" => $"dotFIT approved product copy (authority {authority})",
        "infopage" => $"dotFIT official website page (authority {authority})",
        "pdsrg" => $"Practitioner Dietary Supplement Reference Guide (authority {authority})",
        "qa" => $"dotFIT nutrition knowledge base (authority {authority})",
        "podcast" => $"dotFIT expert discussion transcript (authority {authority})",
        "menu_desc" => $"dotFIT menu description (authority {authority})",
        _ => $"{sourceType} (authority {authority})",
    };

    /// <summary>Short kind for citation lines.</summary>
    public static string SourceKind(string sourceType) => sourceType switch
    {
        "product" => "product copy",
        "infopage" => "dotFIT website",
        "pdsrg" => "practitioner guide",
        "qa" => "customer Q&A",
        "podcast" => "podcast",
        "menu_desc" => "menu",
        _ => sourceType,
    };
}
