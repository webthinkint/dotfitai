using DotFit.Assistant.Aliases;
using DotFit.Assistant.Prompting;

namespace DotFit.Assistant.Tests;

/// <summary>
/// Structure, not wording. The first group runs over every variant in
/// <c>assistant/prompt/</c>, so a new variant is covered without a new test;
/// the rest pin the template rules on small synthetic folders.
/// </summary>
public class PromptTemplateTests
{
    // ---- every variant in the repo -------------------------------------------

    [Fact]
    public void The_default_variant_exists()
    {
        Assert.Contains(PromptTemplate.DefaultVariant, PromptTemplate.Variants(RepoPromptDir()));
    }

    [Fact]
    public void Every_variant_assembles_with_nothing_left_unresolved()
    {
        string dir = RepoPromptDir();
        AliasTable aliases = Fixtures.Aliases();
        foreach (string variant in PromptTemplate.Variants(dir))
        {
            PromptTemplate template = PromptTemplate.Load(dir, variant);
            foreach (string? contact in new[] { "support@example.com", null })
            {
                string text = template.Render(aliases, contact);
                Assert.False(string.IsNullOrWhiteSpace(text), variant);
                Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
                Assert.DoesNotContain("}}", text, StringComparison.Ordinal);
                Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
                Assert.DoesNotContain("-->", text, StringComparison.Ordinal);
                Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_variant_carries_every_required_part_and_the_generated_blocks()
    {
        string dir = RepoPromptDir();
        AliasTable aliases = Fixtures.Aliases();
        foreach (string variant in PromptTemplate.Variants(dir))
        {
            string text = PromptTemplate.Load(dir, variant).Render(aliases, "support@example.com");
            foreach (string required in PromptTemplate.RequiredParts)
            {
                string part = PromptTemplate.Clean(File.ReadAllText(Path.Combine(dir, required + ".md")));
                Assert.Contains(part, text, StringComparison.Ordinal);
            }
            Assert.Contains(SystemPrompt.CurrencyFacts(aliases), text, StringComparison.Ordinal);
            Assert.Contains("support@example.com", text, StringComparison.Ordinal);
        }
    }

    // ---- the template rules --------------------------------------------------

    [Fact]
    public void Parts_nest_and_generated_blocks_fill_in()
    {
        using var dir = Folder(
            ("variants/v.md", "{{outer}}"),
            ("outer.md", "A {{inner}} {{support-team}}"),
            ("inner.md", "B {{escalation-list}}"),
            ("escalation-list.md", "- E"));

        PromptTemplate template = PromptTemplate.Load(dir.Path, "v");

        Assert.Equal("A B - E dotFIT support (help@example.com)", template.Render(Fixtures.Aliases(), "help@example.com"));
        Assert.Equal("A B - E dotFIT support", template.Render(Fixtures.Aliases(), null));
    }

    [Fact]
    public void Comments_are_stripped_without_leaving_blank_lines()
    {
        using var dir = Folder(
            ("variants/v.md", "<!--\nwhy this order\n-->\n\n{{a}}\n"),
            ("a.md", "<!-- about a -->\nline one\n<!-- between -->\nline two <!-- inline -->\n"),
            ("escalation-list.md", "<!-- x -->"));
        string variant = File.ReadAllText(Path.Combine(dir.Path, "variants", "v.md"));
        File.WriteAllText(Path.Combine(dir.Path, "variants", "v.md"), variant.Replace("{{a}}", "{{a}}{{escalation-list}}"));

        Assert.Equal("line one\nline two ", PromptTemplate.Load(dir.Path, "v").Render(Fixtures.Aliases(), null));
    }

    [Fact]
    public void Windows_line_endings_render_the_same_text()
    {
        using var lf = Folder(("variants/v.md", "{{a}}\n\n{{escalation-list}}\n"), ("a.md", "one\ntwo\n"), ("escalation-list.md", "- E\n"));
        using var crlf = Folder(("variants/v.md", "{{a}}\r\n\r\n{{escalation-list}}\r\n"), ("a.md", "one\r\ntwo\r\n"), ("escalation-list.md", "- E\r\n"));

        Assert.Equal(
            PromptTemplate.Load(lf.Path, "v").Render(Fixtures.Aliases(), null),
            PromptTemplate.Load(crlf.Path, "v").Render(Fixtures.Aliases(), null));
    }

    [Fact]
    public void An_unknown_include_is_named()
    {
        using var dir = Folder(("variants/v.md", "{{nope}} {{escalation-list}}"), ("escalation-list.md", "- E"));

        var error = Assert.Throws<PromptException>(() => PromptTemplate.Load(dir.Path, "v"));
        Assert.Contains("{{nope}}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variant_without_a_required_part_does_not_load()
    {
        using var dir = Folder(("variants/v.md", "{{a}}"), ("a.md", "text"), ("escalation-list.md", "- E"));

        var error = Assert.Throws<PromptException>(() => PromptTemplate.Load(dir.Path, "v"));
        Assert.Contains("{{escalation-list}}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_include_loop_does_not_load()
    {
        using var dir = Folder(
            ("variants/v.md", "{{a}} {{escalation-list}}"), ("a.md", "{{b}}"), ("b.md", "{{a}}"), ("escalation-list.md", "- E"));

        var error = Assert.Throws<PromptException>(() => PromptTemplate.Load(dir.Path, "v"));
        Assert.Contains("a -> b -> a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_cannot_shadow_a_generated_block()
    {
        using var dir = Folder(
            ("variants/v.md", "{{currency-facts}} {{escalation-list}}"), ("currency-facts.md", "hand-written"), ("escalation-list.md", "- E"));

        Assert.Throws<PromptException>(() => PromptTemplate.Load(dir.Path, "v"));
    }

    [Fact]
    public void An_unknown_variant_lists_the_ones_that_exist()
    {
        using var dir = Folder(("variants/one.md", "{{escalation-list}}"), ("escalation-list.md", "- E"));

        var error = Assert.Throws<PromptException>(() => PromptTemplate.Load(dir.Path, "two"));
        Assert.Contains("one", error.Message, StringComparison.Ordinal);
        Assert.Throws<PromptException>(() => PromptTemplate.Load(dir.Path, "../escape"));
    }

    [Fact]
    public void The_version_follows_the_text()
    {
        Assert.Equal(PromptTemplate.Version("same"), PromptTemplate.Version("same"));
        Assert.NotEqual(PromptTemplate.Version("same"), PromptTemplate.Version("same."));
        Assert.Equal(12, PromptTemplate.Version("x").Length);
    }

    // ---- helpers -------------------------------------------------------------

    private static TempDir Folder(params (string Path, string Text)[] files)
    {
        var dir = new TempDir("prompt");
        Directory.CreateDirectory(Path.Combine(dir.Path, PromptTemplate.VariantsFolder));
        foreach ((string path, string text) in files)
            File.WriteAllText(Path.Combine(dir.Path, path), text);
        return dir;
    }

    internal static string RepoPromptDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "assistant", "prompt");
            if (Directory.Exists(Path.Combine(candidate, PromptTemplate.VariantsFolder)))
                return candidate;
        }
        throw new DirectoryNotFoundException("assistant/prompt not found from the test output directory");
    }
}
