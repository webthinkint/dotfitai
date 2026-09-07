using DotFit.Agents.Config;
using System.IO;

namespace DotFit.Agents.Tests;

public class RuntimeOptionsTests
{
    private const string ValidEnv = """
        AZURE_SEARCH_ENDPOINT=https://search.example.net
        AZURE_SEARCH_ADMIN_KEY=admin-key-value
        AZURE_OPENAI_ENDPOINT=https://openai.example.net/services/ai
        AZURE_OPENAI_API_KEY=api-key-value
        AZURE_OPENAI_CHAT_DEPLOYMENT=chat-model
        AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT=small-model
        AZURE_OPENAI_EMBEDDING_DEPLOYMENT=embed-model
        """;

    private static string Root() => new TempDir("opts").Path;
    private static string EnvPath(string root) => Path.Combine(root, ".env");

    internal static string WriteEnv(string root, string content)
    {
        string path = EnvPath(root);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadsAllFields()
    {
        string root = Root();
        WriteEnv(root, ValidEnv);
        var o = RuntimeOptions.Load(EnvPath(root));
        Assert.Equal("https://search.example.net/", o.SearchEndpoint.ToString());
        Assert.Equal("chat-model", o.ChatDeployment);
        Assert.Equal("small-model", o.SmallChatDeployment);
        Assert.Equal("embed-model", o.EmbeddingDeployment);
        Assert.False(o.UsingQueryKey); // admin key fallback
        Assert.Equal(Path.Combine(root, "processed", "aliases", "alias_table.json"), o.AliasTablePath);
    }

    [Fact]
    public void QueryKeyIsPreferredOverAdminKey()
    {
        string root = Root();
        WriteEnv(root, ValidEnv + "\nAZURE_SEARCH_QUERY_KEY=query-key-value\n");
        var o = RuntimeOptions.Load(EnvPath(root));
        Assert.True(o.UsingQueryKey);
        Assert.Equal("query-key-value", o.SearchKey);
    }

    [Fact]
    public void SmallChatFallsBackToChatDeployment()
    {
        string root = Root();
        WriteEnv(root, ValidEnv.Replace("AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT=small-model\n", ""));
        var o = RuntimeOptions.Load(EnvPath(root));
        Assert.Equal("chat-model", o.SmallChatDeployment);
    }

    [Fact]
    public void MissingRequiredVariableIsNamedInError()
    {
        string root = Root();
        WriteEnv(root, ValidEnv.Replace("AZURE_OPENAI_CHAT_DEPLOYMENT=chat-model\n", ""));
        var e = Assert.Throws<EnvFile.EnvFileException>(() => RuntimeOptions.Load(EnvPath(root)));
        Assert.Contains("AZURE_OPENAI_CHAT_DEPLOYMENT", e.Message);
    }

    [Fact]
    public void MissingSearchKeyNamesBothKeyVariables()
    {
        string root = Root();
        WriteEnv(root, ValidEnv.Replace("AZURE_SEARCH_ADMIN_KEY=admin-key-value\n", ""));
        var e = Assert.Throws<EnvFile.EnvFileException>(() => RuntimeOptions.Load(EnvPath(root)));
        Assert.Contains("AZURE_SEARCH_QUERY_KEY", e.Message);
        Assert.Contains("AZURE_SEARCH_ADMIN_KEY", e.Message);
    }

    [Fact]
    public void PlaceholderValueIsRejectedWithVariableName()
    {
        string root = Root();
        WriteEnv(root, ValidEnv.Replace("api-key-value", "<your-key-here>"));
        var e = Assert.Throws<EnvFile.EnvFileException>(() => RuntimeOptions.Load(EnvPath(root)));
        Assert.Contains("AZURE_OPENAI_API_KEY", e.Message);
        Assert.DoesNotContain("your-key-here", e.Message); // errors name variables, never values
    }

    [Fact]
    public void BadEndpointIsRejected()
    {
        string root = Root();
        WriteEnv(root, ValidEnv.Replace("https://search.example.net", "not a url"));
        Assert.Throws<EnvFile.EnvFileException>(() => RuntimeOptions.Load(EnvPath(root)));
    }

    [Fact]
    public void ToStringMasksKeysButShowsDeployments()
    {
        string root = Root();
        WriteEnv(root, ValidEnv + "\nAZURE_SEARCH_QUERY_KEY=query-key-value\n");
        string rendered = RuntimeOptions.Load(EnvPath(root)).ToString();
        Assert.Contains("chat='chat-model'", rendered);
        Assert.DoesNotContain("api-key-value", rendered);
        Assert.DoesNotContain("query-key-value", rendered);
        Assert.DoesNotContain("admin-key-value", rendered);
        Assert.Contains("***", rendered);
    }

    [Fact]
    public void DiscoveryWalksUpToTheEnvFile()
    {
        string root = Root();
        WriteEnv(root, ValidEnv);
        string nested = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(nested);
        var o = RuntimeOptions.Load(null, startDir: nested);
        Assert.Equal("chat-model", o.ChatDeployment);
        Assert.Equal(root, Path.GetDirectoryName(o.EnvFilePath));
    }

    [Fact]
    public void MissingEnvFileNamesThePathAndTheTemplate()
    {
        string root = Root();
        var e = Assert.Throws<EnvFile.EnvFileException>(() => RuntimeOptions.Load(EnvPath(root)));
        Assert.Contains(".env.example", e.Message);
    }
}
