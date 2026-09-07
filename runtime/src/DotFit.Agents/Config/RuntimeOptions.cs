namespace DotFit.Agents.Config;

/// <summary>
/// The runtime view of the <c>.env</c> contract (plan §10): endpoints and
/// deployments in the clear, keys never rendered. The query-only search key is
/// preferred over the admin key when present — the runtime *is* the query-only
/// answer service that variable was waiting for.
/// </summary>
public sealed record RuntimeOptions
{
    public const string SearchEndpointVar = "AZURE_SEARCH_ENDPOINT";
    public const string SearchAdminKeyVar = "AZURE_SEARCH_ADMIN_KEY";
    public const string SearchQueryKeyVar = "AZURE_SEARCH_QUERY_KEY";
    public const string OpenAiEndpointVar = "AZURE_OPENAI_ENDPOINT";
    public const string OpenAiApiKeyVar = "AZURE_OPENAI_API_KEY";
    public const string ChatDeploymentVar = "AZURE_OPENAI_CHAT_DEPLOYMENT";
    public const string SmallChatDeploymentVar = "AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT";
    public const string EmbeddingDeploymentVar = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT";

    public required Uri SearchEndpoint { get; init; }
    public required string SearchKey { get; init; }
    /// <summary>True when the query-only key is in use (admin key not needed).</summary>
    public required bool UsingQueryKey { get; init; }
    public required Uri OpenAiEndpoint { get; init; }
    public required string OpenAiApiKey { get; init; }
    /// <summary>Grounded-answer deployment (frontier model when quota lands).</summary>
    public required string ChatDeployment { get; init; }
    /// <summary>Guardrail / rewrite / claims-check deployment; falls back to <see cref="ChatDeployment"/>.</summary>
    public required string SmallChatDeployment { get; init; }
    public required string EmbeddingDeployment { get; init; }
    public string IndexName { get; init; } = "kb-main";
    /// <summary>Alias table artifact (plan §5 output) — default lives beside the .env.</summary>
    public required string AliasTablePath { get; init; }
    public required string EnvFilePath { get; init; }

    /// <summary>
    /// Load and validate. Without <paramref name="envPath"/>, walks up from
    /// <paramref name="startDir"/> (default: the current directory) until a
    /// <c>.env</c> appears — so `dotnet run` from runtime/ finds the repo root.
    /// </summary>
    public static RuntimeOptions Load(string? envPath = null, string? startDir = null)
    {
        string path = envPath ?? DiscoverEnvPath(startDir ?? Environment.CurrentDirectory);
        if (!File.Exists(path))
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName} not found at {path} — copy {EnvFile.ExampleFileName} to " +
                $"{EnvFile.FileName} and fill it in, or pass --env <path>");
        var values = EnvFile.ReadFile(path);
        string envDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return FromValues(values, envDir, Path.GetFullPath(path));
    }

    /// <summary>Validate a parsed .env into options. Errors name variables, never values.</summary>
    internal static RuntimeOptions FromValues(
        IReadOnlyDictionary<string, string> values, string envDir, string envFilePath)
    {
        Uri Endpoint(string name)
        {
            string v = Required(values, name);
            if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new EnvFile.EnvFileException(
                    $"{EnvFile.FileName}: {name} must be an absolute http(s) URL — see {EnvFile.ExampleFileName}");
            return uri;
        }

        Uri searchEndpoint = Endpoint(SearchEndpointVar);
        Uri openAiEndpoint = Endpoint(OpenAiEndpointVar);

        string? queryKey = Optional(values, SearchQueryKeyVar);
        string? adminKey = Optional(values, SearchAdminKeyVar);
        string? searchKey = queryKey ?? adminKey;
        if (searchKey is null)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: either {SearchQueryKeyVar} or {SearchAdminKeyVar} must be set " +
                $"— see {EnvFile.ExampleFileName}");

        string chat = Required(values, ChatDeploymentVar);
        string small = Optional(values, SmallChatDeploymentVar) ?? chat;
        string embedding = Required(values, EmbeddingDeploymentVar);
        string apiKey = Required(values, OpenAiApiKeyVar);

        return new RuntimeOptions
        {
            SearchEndpoint = searchEndpoint,
            SearchKey = searchKey,
            UsingQueryKey = queryKey is not null,
            OpenAiEndpoint = openAiEndpoint,
            OpenAiApiKey = apiKey,
            ChatDeployment = chat,
            SmallChatDeployment = small,
            EmbeddingDeployment = embedding,
            AliasTablePath = Path.Combine(envDir, "processed", "aliases", "alias_table.json"),
            EnvFilePath = envFilePath,
        };
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
    {
        string v = values.TryGetValue(name, out var s) ? s.Trim() : "";
        if (v.Length == 0)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: required variable {name} is missing or empty — see {EnvFile.ExampleFileName}");
        return CheckPlaceholder(v, name);
    }

    private static string? Optional(IReadOnlyDictionary<string, string> values, string name)
    {
        string v = values.TryGetValue(name, out var s) ? s.Trim() : "";
        if (v.Length == 0)
            return null;
        return CheckPlaceholder(v, name); // a broken optional is still a broken .env
    }

    private static string CheckPlaceholder(string v, string name)
    {
        if (v.Contains('<') || v.Contains('>'))
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {name} still contains a <placeholder> — fill in the real value or delete the line");
        return v;
    }

    private static string DiscoverEnvPath(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, EnvFile.FileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(Path.GetFullPath(startDir), EnvFile.FileName); // for the "not found at" error
    }

    /// <summary>Masked representation — safe for logs and chat. Keys are never shown.</summary>
    public override string ToString() =>
        $"RuntimeOptions(search={SearchEndpoint}, search_key={(UsingQueryKey ? "query" : "admin")}***, " +
        $"openai={OpenAiEndpoint}, api_key=***, chat='{ChatDeployment}', small='{SmallChatDeployment}', " +
        $"embed='{EmbeddingDeployment}', index='{IndexName}', aliases='{AliasTablePath}')";
}
