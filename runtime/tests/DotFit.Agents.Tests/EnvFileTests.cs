using DotFit.Agents.Config;
using System.IO;

namespace DotFit.Agents.Tests;

public class EnvFileTests
{
    [Fact]
    public void ParsesValuesCommentsAndQuotes()
    {
        var values = EnvFile.Parse("""
            # contract template
            A=plain
            export EXPORTED=yes

            B=with spaces  # trailing comment
            C='quoted # not a comment'
            D="double"
            E=#
            F=unquoted#nospace
            """);
        Assert.Equal("plain", values["A"]);
        Assert.Equal("yes", values["EXPORTED"]);
        Assert.Equal("with spaces", values["B"]);
        Assert.Equal("quoted # not a comment", values["C"]);
        Assert.Equal("double", values["D"]);
        Assert.Equal("", values["E"]);
        Assert.Equal("unquoted#nospace", values["F"]); // no whitespace before # → not a comment
    }

    [Fact]
    public void MalformedLineRaisesWithLineNumber()
    {
        var e = Assert.Throws<EnvFile.EnvFileException>(() => EnvFile.Parse("OK=1\nnot-a-pair"));
        Assert.Contains("line 2", e.Message);
    }

    [Fact]
    public void DuplicateKeyRaises()
    {
        var e = Assert.Throws<EnvFile.EnvFileException>(() => EnvFile.Parse("A=1\nA=2"));
        Assert.Contains("duplicate key A", e.Message);
    }

    [Fact]
    public void BadKeyNameRaises()
    {
        Assert.Throws<EnvFile.EnvFileException>(() => EnvFile.Parse("1BAD=x"));
        Assert.Throws<EnvFile.EnvFileException>(() => EnvFile.Parse("BAD-KEY=x"));
    }

    [Fact]
    public void ReadFileToleratesWindowsBom()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, ".env");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "X=1"u8.ToArray()]);
        Assert.Equal("1", EnvFile.ReadFile(path)["X"]);
    }
}
