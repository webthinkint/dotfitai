using DotFit.Agents.Aliases;

namespace DotFit.Agents.Tests;

public class AliasTableTests
{
    // Synthetic fixture — no real corpus text (same rule as the pipeline tests).
    private const string Table = """
        {
          "version": "test-1.0.0",
          "families": [
            {"family": "Test Family", "canonical_part_no": 9001, "part_nos": [9001], "n_variants": 1},
            {"family": "Over 50 Test", "canonical_part_no": 9009, "part_nos": [9009], "n_variants": 1},
            {"family": "Pre & Post Test", "canonical_part_no": 9100, "part_nos": [9100, 9101], "n_variants": 2}
          ],
          "deterministic_aliases": [
            {"token": "TB", "family": "Test Family", "part_nos": [9001], "source": "fixture"},
            {"token": "1-Test", "family": "Test Family", "part_nos": [9001], "source": "fixture"},
            {"token": "Super Test Blend", "family": "Test Family", "part_nos": [9001], "source": "fixture"}
          ],
          "llm_only_aliases": [
            {"token": "Women’s", "family": "Over 50 Test", "part_nos": [9009], "source": "fixture",
             "note": "also ordinary English"}
          ],
          "context_only_tokens": {
            "TT": "generic 'test tonic' — resolve contextually, never a blanket tag"
          },
          "legacy_renames": [
            {"deprecated": "OldTest", "current_family": "Test Family", "part_nos": [9001], "source": "fixture"}
          ],
          "replacements": [
            {"deprecated": "GoneTest", "successor_family": "Test Family", "successor_part_nos": [9001],
             "note": "different formula", "source": "fixture"}
          ],
          "discontinued": [
            {"name": "DeadTest", "note": "discontinued — see Test Family for adults"}
          ]
        }
        """;

    private static AliasTable Load() => AliasTable.FromJson(Table);

    [Fact]
    public void MissingSectionRaises()
    {
        Assert.Throws<AliasTableException>(() => AliasTable.FromJson("""{"version":"x"}"""));
        Assert.Throws<AliasTableException>(() => AliasTable.FromJson("""not json"""));
    }

    [Fact]
    public void MentionPathResolvesFamilyNamesTokensAndLlmOnlyTier()
    {
        var t = Load();
        var e = t.Expand("unrelated question", ["Test Family", "TB", "Women's"]);
        Assert.Equal(["Test Family", "Over 50 Test"], e.Families);
        Assert.Equal([9001, 9009], e.PartNos);
    }

    [Fact]
    public void BlindPathResolvesDeterministicTierOnly()
    {
        var t = Load();
        // "Women's" is the LLM-only tier: a blind scan of raw text must NOT resolve it.
        var e = t.Expand("should I take women's vitamins with TB?");
        Assert.Equal(["Test Family"], e.Families);
        Assert.DoesNotContain(e.Families, f => f == "Over 50 Test");
    }

    [Fact]
    public void ShortTokensRequireWordBoundaries()
    {
        var t = Load();
        // "TT" must not match inside "happy" or "bottle" — the word-join matcher.
        Assert.Empty(t.Expand("this happy bottle is little").Notes);
        var e = t.Expand("what about TT for me?");
        Assert.Contains(e.Notes, n => n.Contains("TT") && n.Contains("test tonic"));
    }

    [Fact]
    public void MultiWordAndHyphenatedTokensMatch()
    {
        var t = Load();
        Assert.Equal(["Test Family"], t.Expand("is Super Test Blend good?").Families);
        Assert.Equal(["Test Family"], t.Expand("the 1-Test tablets").Families);
        Assert.Equal(["Over 50 Test"], t.Expand("over 50 test formula").Families);
        Assert.Equal(["Pre & Post Test"], t.Expand("Pre & Post Test for workouts").Families);
    }

    [Fact]
    public void LegacyRenameIsIdentity_FamilyPlusPartNosPlusNote()
    {
        var t = Load();
        var e = t.Expand("where did OldTest go?");
        Assert.Equal(["Test Family"], e.Families);
        Assert.Equal([9001], e.PartNos);
        Assert.Contains(e.Notes, n => n.Contains("OldTest was renamed Test Family"));
        Assert.Contains("Test Family", e.SearchTerms);
    }

    [Fact]
    public void ReplacementIsNotIdentity_NoteOnly()
    {
        var t = Load();
        var e = t.Expand("can I still buy GoneTest?");
        Assert.Contains(e.Notes, n => n.Contains("GoneTest is discontinued and was replaced by Test Family"));
        Assert.Empty(e.Families); // no identity expansion — a different formula
    }

    [Fact]
    public void DiscontinuedNameProducesGuidanceNoteOnly()
    {
        var t = Load();
        var e = t.Expand("is DeadTest safe for kids?");
        Assert.Contains(e.Notes, n => n.Contains("DeadTest is discontinued"));
        Assert.Empty(e.PartNos);
    }

    [Fact]
    public void UnresolvedMentionsAreTraceOnly()
    {
        var t = Load();
        var e = t.Expand("question", ["Some Third Party Product"]);
        Assert.Empty(e.Families);
        Assert.Empty(e.Notes);
    }

    [Fact]
    public void CurlyApostropheFoldsOntoToken()
    {
        var t = Load();
        var e = t.Expand("q", ["Women’s"]); // U+2019, the corpus spelling
        Assert.Equal(["Over 50 Test"], e.Families);
    }
}
