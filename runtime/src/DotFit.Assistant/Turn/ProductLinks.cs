using System.Globalization;
using System.Text.RegularExpressions;
using DotFit.Assistant.Aliases;

namespace DotFit.Assistant.Turn;

/// <summary>
/// A dotFIT product the answer recommends, as the caller renders it: the family
/// and every part number (SKU) it sells under, so one product is one card.
/// </summary>
public sealed record ProductRef(string Family, IReadOnlyList<string> PartNos);

/// <summary>
/// Product links: how the answer says which products it recommends.
///
/// The model links a product it recommends, or that the answer is about, as
/// <c>[WheySmooth](dotfit:product/1369)</c>, and leaves unlinked one it excludes
/// or advises against. That is the difference a scan for product names cannot
/// see: "leave out ThermAccel" names ThermAccel. The links are read off the
/// finished answer and the text is never changed. Each part number is resolved
/// through the alias table, so a SKU on the wire is one the catalog attests, and
/// a link whose number resolves to no current product is counted, not listed.
/// </summary>
public static partial class ProductLinks
{
    /// <summary>The link target's prefix; the part number follows it.</summary>
    public const string Scheme = "dotfit:product/";

    /// <summary>
    /// The linked products, in order of first link and one per family, and the
    /// number of links that resolved to none.
    /// </summary>
    public static (IReadOnlyList<ProductRef> Products, int Unresolved) In(string answer, AliasTable aliases)
    {
        var products = new List<ProductRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int unresolved = 0;
        foreach (Match link in LinkRegex().Matches(answer))
        {
            if (!int.TryParse(link.Groups["ref"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int partNo)
                || aliases.FamilyOfPartNo(partNo) is not { } family)
            {
                unresolved++;
                continue;
            }
            if (seen.Add(family.Family))
                products.Add(new ProductRef(
                    family.Family, [.. family.PartNos.Select(p => p.ToString(CultureInfo.InvariantCulture))]));
        }
        return (products, unresolved);
    }

    // Any target under the scheme, so a malformed one is counted rather than unseen.
    [GeneratedRegex(@"\]\(dotfit:product/(?<ref>[^)\s]*)\)")]
    private static partial Regex LinkRegex();
}
