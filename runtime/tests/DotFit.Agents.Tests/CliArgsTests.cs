using DotFit.Agents.Cli;

namespace DotFit.Agents.Tests;

public class CliArgsTests
{
    [Fact]
    public void ParsesVerbQuestionAndFlags()
    {
        CliCommand c = CliArgs.Parse(
            ["ask", "can", "I", "take", "creatine", "--trace", "--top", "5", "--env", "C:/tmp/env.txt"]);
        Assert.Equal("ask", c.Verb);
        Assert.Equal("can I take creatine", c.Text);
        Assert.True(c.Flags.Trace);
        Assert.Equal(5, c.Flags.Top);
        Assert.Equal("C:/tmp/env.txt", c.Flags.EnvPath);
    }

    [Fact]
    public void QuestionFlagsMayInterleave()
    {
        CliCommand c = CliArgs.Parse(["ask", "--no-stream", "what", "is", "in", "--trace", "TestBlend"]);
        Assert.Equal("what is in TestBlend", c.Text);
        Assert.True(c.Flags.NoStream);
        Assert.True(c.Flags.Trace);
    }

    [Fact]
    public void SemanticTogglesBothWays()
    {
        Assert.True(CliArgs.Parse(["search", "q", "--semantic"]).Flags.Semantic);
        Assert.False(CliArgs.Parse(["search", "q", "--no-semantic"]).Flags.Semantic);
        Assert.Null(CliArgs.Parse(["search", "q"]).Flags.Semantic);
    }

    [Fact]
    public void SearchRawAndJsonFlags()
    {
        var c = CliArgs.Parse(["search", "creatine loading", "--raw", "--json", "--filter", "products/any(p: p eq '9001')"]);
        Assert.Equal("creatine loading", c.Text);
        Assert.True(c.Flags.Raw);
        Assert.True(c.Flags.Json);
        Assert.Equal("products/any(p: p eq '9001')", c.Flags.Filter);
    }

    [Fact]
    public void ChatNeedsNoQuestion()
    {
        Assert.Equal("chat", CliArgs.Parse(["chat"]).Verb);
        Assert.Equal("", CliArgs.Parse(["chat"]).Text);
    }

    [Fact]
    public void OtherVerbsNeedAQuestion()
    {
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["ask"]));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["search"]));
    }

    [Fact]
    public void UnknownVerbOrFlagRaisesWithUsage()
    {
        var e1 = Assert.Throws<CliUsageException>(() => CliArgs.Parse(["fly", "q"]));
        Assert.Contains("unknown command", e1.Message);
        var e2 = Assert.Throws<CliUsageException>(() => CliArgs.Parse(["ask", "q", "--bogus"]));
        Assert.Contains("unknown flag", e2.Message);
        var e3 = Assert.Throws<CliUsageException>(() => CliArgs.Parse(["ask", "q", "--top", "zero"]));
        Assert.Contains("--top", e3.Message);
        var e4 = Assert.Throws<CliUsageException>(() => CliArgs.Parse(["ask", "q", "--env"]));
        Assert.Contains("needs a value", e4.Message);
    }

    [Fact]
    public void HelpSurfacesUsage()
    {
        var e = Assert.Throws<CliUsageException>(() => CliArgs.Parse(["help"]));
        Assert.Contains("dotfit-agent", e.Message);
    }

    [Fact]
    public void GatedIsOffByDefaultAndOptIn()
    {
        Assert.False(CliArgs.Parse(["ask", "q"]).Flags.Gated);
        Assert.True(CliArgs.Parse(["ask", "--gated", "q"]).Flags.Gated);
    }
}
