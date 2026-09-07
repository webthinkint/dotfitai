namespace DotFit.Agents.Config;

/// <summary>
/// Which slice of the <c>.env</c> contract a caller needs — the .NET mirror of
/// <c>azure_config.py</c>'s <c>require=</c> subsets, and for the same reason:
/// the two chat deployments are pending a quota increase (open item 1), so
/// retrieval-only work must not demand them. A flags enum rather than Python's
/// list of variable names, which makes the compiler the typo guard.
/// </summary>
[Flags]
public enum RuntimeNeeds
{
    None = 0,
    /// <summary>AI Search endpoint + one of the query/admin keys.</summary>
    Search = 1 << 0,
    /// <summary>Azure OpenAI endpoint + key + the embedding deployment.</summary>
    Embedding = 1 << 1,
    /// <summary>Azure OpenAI endpoint + key + the grounded-answer deployment.</summary>
    Chat = 1 << 2,
    /// <summary>Azure OpenAI endpoint + key + the small deployment (or the chat one).</summary>
    SmallChat = 1 << 3,
    /// <summary>The <c>search</c> verb: embed the query, run the hybrid query. No chat model.</summary>
    Retrieval = Search | Embedding,
    /// <summary>The <c>ask</c> / <c>chat</c> verbs: the whole §11 pipeline.</summary>
    Full = Search | Embedding | Chat | SmallChat,
}

/// <summary>
/// The runtime view of the <c>.env</c> contract (plan §10): endpoints and
/// deployments in the clear, keys never rendered. The query-only search key is
/// preferred over the admin key when present — the runtime *is* the query-only
/// answer service that variable was waiting for.
///
/// Every value is nullable, exactly as <c>AzureConfig</c>'s fields are: what
/// gets loaded depends on the <see cref="RuntimeNeeds"/> subset the caller asks
/// for. Values that *are* present are always validated, required or not — a
/// broken optional is still a broken <c>.env</c>. Call the <c>Require*</c>
/// accessors at the point of use; they throw naming the variable, so a caller
/// that outgrows its subset says which variable it outgrew.
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

    public Uri? SearchEndpoint { get; init; }
    public string? SearchKey { get; init; }
    /// <summary>True when the query-only key is in use (admin key not needed).</summary>
    public bool UsingQueryKey { get; init; }
    public Uri? OpenAiEndpoint { get; init; }
    public string? OpenAiApiKey { get; init; }
    /// <summary>Grounded-answer deployment (frontier model when quota lands).</summary>
    public string? ChatDeployment { get; init; }
    /// <summary>Guardrail / rewrite / claims-check deployment; falls back to <see cref="ChatDeployment"/>.</summary>
    public string? SmallChatDeployment { get; init; }
    public string? EmbeddingDeployment { get; init; }
    /// <summary>The subset this instance was loaded for.</summary>
    public RuntimeNeeds Needs { get; init; } = RuntimeNeeds.Full;
    public string IndexName { get; init; } = "kb-main";
    /// <summary>Alias table artifact (plan §5 output) — default lives beside the .env.</summary>
    public required string AliasTablePath { get; init; }
    public required string EnvFilePath { get; init; }

    public Uri RequireSearchEndpoint() => SearchEndpoint ?? throw Missing(SearchEndpointVar);
    public string RequireSearchKey() => SearchKey ?? throw MissingEitherKey();
    public Uri RequireOpenAiEndpoint() => OpenAiEndpoint ?? throw Missing(OpenAiEndpointVar);
    public string RequireOpenAiApiKey() => OpenAiApiKey ?? throw Missing(OpenAiApiKeyVar);
    public string RequireChatDeployment() => ChatDeployment ?? throw Missing(ChatDeploymentVar);
    public string RequireSmallChatDeployment() => SmallChatDeployment ?? throw Missing(SmallChatDeploymentVar);
    public string RequireEmbeddingDeployment() => EmbeddingDeployment ?? throw Missing(EmbeddingDeploymentVar);

    /// <summary>
    /// Load and validate the <paramref name="needs"/> subset. Without
    /// <paramref name="envPath"/>, walks up from <paramref name="startDir"/>
    /// (default: the current directory) until a <c>.env</c> appears — so
    /// <c>dotnet run</c> from runtime/ finds the repo root.
    /// </summary>
    public static RuntimeOptions Load(
        string? envPath = null, string? startDir = null, RuntimeNeeds needs = RuntimeNeeds.Full)
    {
        string path = envPath ?? DiscoverEnvPath(startDir ?? Environment.CurrentDirectory);
        if (!File.Exists(path))
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName} not found at {path} — copy {EnvFile.ExampleFileName} to " +
                $"{EnvFile.FileName} and fill it in, or pass --env <path>");
        var values = EnvFile.ReadFile(path);
        string envDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return FromValues(values, envDir, Path.GetFullPath(path), needs);
    }

    /// <summary>Validate a parsed .env into options. Errors name variables, never values.</summary>
    internal static RuntimeOptions FromValues(
        IReadOnlyDictionary<string, string> values, string envDir, string envFilePath,
        RuntimeNeeds needs = RuntimeNeeds.Full)
    {
        bool Needed(RuntimeNeeds n) => (needs & n) != 0;
        bool openAiNeeded = Needed(RuntimeNeeds.Embedding | RuntimeNeeds.Chat | RuntimeNeeds.SmallChat);

        string? Value(string name, bool required)
        {
            string v = values.TryGetValue(name, out var s) ? s.Trim() : "";
            if (v.Length == 0)
                return required ? throw Missing(name) : null;
            if (v.Contains('<') || v.Contains('>'))
                throw new EnvFile.EnvFileException(
                    $"{EnvFile.FileName}: {name} still contains a <placeholder> — " +
                    "fill in the real value or delete the line");
            return v;
        }

        Uri? Endpoint(string name, bool required)
        {
            string? v = Value(name, required);
            if (v is null)
                return null;
            if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new EnvFile.EnvFileException(
                    $"{EnvFile.FileName}: {name} must be an absolute http(s) URL — see {EnvFile.ExampleFileName}");
            return uri;
        }

        Uri? searchEndpoint = Endpoint(SearchEndpointVar, Needed(RuntimeNeeds.Search));
        Uri? openAiEndpoint = Endpoint(OpenAiEndpointVar, openAiNeeded);

        string? queryKey = Value(SearchQueryKeyVar, required: false);
        string? adminKey = Value(SearchAdminKeyVar, required: false);
        string? searchKey = queryKey ?? adminKey;
        if (searchKey is null && Needed(RuntimeNeeds.Search))
            throw MissingEitherKey();

        string? chat = Value(ChatDeploymentVar, Needed(RuntimeNeeds.Chat));
        string? small = Value(SmallChatDeploymentVar, required: false) ?? chat;
        if (small is null && Needed(RuntimeNeeds.SmallChat))
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: required variable {SmallChatDeploymentVar} is missing or empty " +
                $"(and so is {ChatDeploymentVar}, which it falls back to) — see {EnvFile.ExampleFileName}");
        string? embedding = Value(EmbeddingDeploymentVar, Needed(RuntimeNeeds.Embedding));
        string? apiKey = Value(OpenAiApiKeyVar, openAiNeeded);

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
            Needs = needs,
            AliasTablePath = Path.Combine(envDir, "processed", "aliases", "alias_table.json"),
            EnvFilePath = envFilePath,
        };
    }

    private static EnvFile.EnvFileException Missing(string name) =>
        new($"{EnvFile.FileName}: required variable {name} is missing or empty — see {EnvFile.ExampleFileName}");

    private static EnvFile.EnvFileException MissingEitherKey() =>
        new($"{EnvFile.FileName}: either {SearchQueryKeyVar} or {SearchAdminKeyVar} must be set " +
            $"— see {EnvFile.ExampleFileName}");

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
    public override string ToString()
    {
        static string Show(object? value) => value?.ToString() is { Length: > 0 } s ? s : "unset";
        return
            $"RuntimeOptions(needs={Needs}, search={Show(SearchEndpoint)}, " +
            $"search_key={(SearchKey is null ? "unset" : UsingQueryKey ? "query***" : "admin***")}, " +
            $"openai={Show(OpenAiEndpoint)}, api_key={(OpenAiApiKey is null ? "unset" : "***")}, " +
            $"chat='{Show(ChatDeployment)}', small='{Show(SmallChatDeployment)}', " +
            $"embed='{Show(EmbeddingDeployment)}', index='{IndexName}', aliases='{AliasTablePath}')";
    }
}
