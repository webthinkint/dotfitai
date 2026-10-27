using DotFit.Assistant.Turn;

namespace DotFit.Assistant.Tests;

/// <summary>
/// Product links: the answer's own statement of which products it recommends,
/// resolved through the alias table into what the caller renders as cards.
/// </summary>
public class ProductLinkTests
{
    [Fact]
    public void Linked_products_resolve_to_their_family_in_order_of_first_link()
    {
        // Any one of a family's part numbers names the family, and the card
        // carries all of them; a second link to the same product is one card.
        const string answer =
            "Start with [OtherFormula](dotfit:product/9100), then [ExampleFormula](dotfit:product/9002) " +
            "after training. Keep the [ExampleFormula](dotfit:product/9001) to one scoop.";

        (IReadOnlyList<ProductRef> products, int unresolved) = ProductLinks.In(answer, Fixtures.Aliases());

        Assert.Equal(["OtherFormula", "ExampleFormula"], products.Select(p => p.Family));
        Assert.Equal(["9001", "9002"], products[1].PartNos);
        Assert.Equal(0, unresolved);
    }

    [Fact]
    public void An_unlinked_name_is_not_a_product_and_a_bad_link_is_counted_not_listed()
    {
        // "Leave out OtherFormula" names it; only a link says recommend it. A
        // part number the catalog does not attest never becomes a card.
        const string answer =
            "Leave out OtherFormula. Take [ExampleFormula](dotfit:product/9001), not " +
            "[Mystery](dotfit:product/4242) or [Typo](dotfit:product/ExampleFormula).";

        (IReadOnlyList<ProductRef> products, int unresolved) = ProductLinks.In(answer, Fixtures.Aliases());

        Assert.Equal(["ExampleFormula"], products.Select(p => p.Family));
        Assert.Equal(2, unresolved);
    }

    [Fact]
    public void Text_without_links_has_no_products()
    {
        Assert.Empty(ProductLinks.In("Take 5 g daily [1].", Fixtures.Aliases()).Products);
    }
}
