using DotFit.Agentic.Cli;

namespace DotFit.Agentic.Tests;

/// <summary>
/// The committed smoke set parses and every item says what to look for
/// (design §11.2). It runs no model and hits no network — it reads the file.
///
/// The <c>looking_for</c> check is the one with teeth. An item with no stated
/// failure it is watching for is an item nobody will know how to read in three
/// months, and the set's whole value is that a human can read it.
/// </summary>
public class SmokeSetTests
{
    private static string SetPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "smoke", "conversations.jsonl");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("runtime/smoke/conversations.jsonl not found from the test output directory");
    }

    [Fact]
    public void Every_item_parses_and_carries_an_id_a_tier_and_turns()
    {
        IReadOnlyList<SmokeItem> items = Smoke.Load(SetPath());

        Assert.NotEmpty(items);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (SmokeItem item in items)
        {
            Assert.NotEmpty(item.Tier);
            Assert.All(item.Turns, turn => Assert.False(string.IsNullOrWhiteSpace(turn)));
        }
    }

    [Fact]
    public void Every_item_says_what_a_human_should_look_for()
    {
        foreach (SmokeItem item in Smoke.Load(SetPath()))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.LookingFor),
                $"{item.Id} has no looking_for — nobody will know how to read its transcript");
            Assert.True(item.LookingFor.Length > 30,
                $"{item.Id}'s looking_for is too short to be a real instruction");
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("product")]
    [InlineData("currency")]
    [InlineData("multiturn")]
    [InlineData("safety")]
    [InlineData("claims")]
    [InlineData("scope")]
    [InlineData("adversarial")]
    [InlineData("retrieval")]
    public void Every_tier_the_readme_documents_has_at_least_one_item(string tier)
    {
        IReadOnlyList<SmokeItem> items = Smoke.Load(SetPath());
        Assert.Contains(items, i => string.Equals(i.Tier, tier, StringComparison.Ordinal));
    }

    [Fact]
    public void The_multiturn_tier_actually_has_more_than_one_turn()
    {
        foreach (SmokeItem item in Smoke.Load(SetPath()).Where(i => i.Tier is "multiturn"))
            Assert.True(item.Turns.Length > 1, $"{item.Id} is tagged multiturn but has one turn");
    }

    [Fact]
    public void The_safety_tier_covers_the_delayed_trigger_and_its_countervailing_rule()
    {
        // §8.1 holds both directions: a trigger stated once still binds, and it
        // does not put every later turn behind a caveat. A set that only tested
        // the first would reward a model that refuses everything afterwards.
        IReadOnlyList<SmokeItem> safety = [.. Smoke.Load(SetPath()).Where(i => i.Tier is "safety")];

        Assert.Contains(safety, i => i.Turns.Length > 1);
        Assert.Contains(safety, i => i.Turns.Length > 2);
    }
}
