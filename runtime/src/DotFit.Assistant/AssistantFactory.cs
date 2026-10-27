using System.ClientModel;
using Anthropic.Foundry;
using Anthropic.Models.Messages;
using Azure;
using DotFit.Assistant.Cost;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using DotFit.Assistant.Config;
using DotFit.Assistant.Prompting;
using DotFit.Assistant.Retrieval;
using DotFit.Assistant;
using DotFit.Assistant.Aliases;
using DotFit.Assistant.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace DotFit.Assistant;

/// <summary>
/// Wires <see cref="RuntimeOptions"/> and <see cref="AssistantOptions"/> into a
/// live assistant. All the behavior is in the components; this
/// only builds Azure clients and the agent over them.
///
/// One chat deployment runs the loop (<c>AZURE_OPENAI_CHAT_DEPLOYMENT</c>) and
/// <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> embeds queries; nothing else calls
/// a model.
///
/// The chat deployment's name picks the wire. A <c>claude*</c> deployment is
/// called through the resource's Anthropic Messages endpoint
/// (<c>/anthropic/v1/messages</c>); every other deployment through the
/// Responses API. Measured on <c>claude-sonnet-5-5</c>: the Responses API
/// holds a Claude answer back and releases it in one burst, so a turn with no
/// lookup showed its first word after ~6 s instead of ~1 s; on Messages it
/// streams. Both surfaces take the same key and the same resource.
/// </summary>
public static class AssistantFactory
{
    /// <summary>What the loop needs out of the <c>.env</c> contract.</summary>
    public const RuntimeNeeds Needs = RuntimeNeeds.Search | RuntimeNeeds.Embedding | RuntimeNeeds.Chat;

    /// <summary>
    /// The api-version verified live on the Foundry v2 resource. The stable
    /// Azure.AI.OpenAI build only offers 2024-10-21, which that resource 404s,
    /// hence the prerelease package pin.
    /// </summary>
    public const AzureOpenAIClientOptions.ServiceVersion OpenAiServiceVersion =
        AzureOpenAIClientOptions.ServiceVersion.V2025_04_01_Preview;

    public static AzureOpenAIClient CreateOpenAiClient(RuntimeOptions options) => new(
        options.RequireOpenAiEndpoint(),
        new ApiKeyCredential(options.RequireOpenAiApiKey()),
        new AzureOpenAIClientOptions(OpenAiServiceVersion));

    public static SearchClient CreateSearchClient(RuntimeOptions options) => new(
        options.RequireSearchEndpoint(), options.IndexName, new AzureKeyCredential(options.RequireSearchKey()));

    /// <summary>
    /// The agent: the frontier deployment, carrying the assembled system
    /// prompt as its instructions. Tools are *not* bound here — they are
    /// turn-scoped (their ledger and budget are) and arrive as per-run options.
    /// </summary>
    // OPENAI001: the SDK still marks the Responses surface experimental.
#pragma warning disable OPENAI001
    public static AIAgent CreateAgent(
        RuntimeOptions options, AzureOpenAIClient client, AssembledPrompt prompt, string? effort = null)
    {
        string deployment = options.RequireChatDeployment();
        if (IsClaude(deployment))
            return CreateClaudeClient(options).AsIChatClient(deployment).AsAIAgent(ClaudeAgentOptions(prompt.Text, deployment, effort));
        return client.GetResponsesClient().AsAIAgent(AgentOptions(prompt.Text), model: deployment);
    }

    /// <summary>Whether a deployment is called through the Anthropic Messages endpoint.</summary>
    public static bool IsClaude(string deployment) =>
        deployment.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Foundry client for the Messages endpoint. It addresses the resource
    /// by name, so the name is read off <c>AZURE_OPENAI_ENDPOINT</c>; a classic
    /// <c>*.openai.azure.com</c> endpoint serves no Claude and fails the boot.
    /// </summary>
    public static AnthropicFoundryClient CreateClaudeClient(RuntimeOptions options)
    {
        Uri endpoint = options.RequireOpenAiEndpoint();
        const string suffix = ".services.ai.azure.com";
        if (!endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{RuntimeOptions.OpenAiEndpointVar} must be a Foundry resource (https://<name>{suffix}) " +
                "to call a claude deployment.");
        string resource = endpoint.Host[..^suffix.Length];
        return new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(options.RequireOpenAiApiKey(), resource));
    }

    /// <summary>
    /// The agent's options, apart from the client, so the loop tests run the
    /// same configuration production does.
    /// </summary>
    public static ChatClientAgentOptions AgentOptions(string instructions) => BaseAgentOptions(instructions,
        // Responses API: reasoning deployments reject tools on Chat Completions.
        // Storage off: a stored response would keep the customer's question on Azure.
        _ => new CreateResponseOptions { StoredOutputEnabled = false });

    /// <summary>
    /// The output cap for a Claude round trip. Messages requires one, and
    /// thinking counts against it; this is room for both, not a target.
    /// </summary>
    public const int ClaudeMaxOutputTokens = 32_000;

    /// <summary>
    /// <see cref="AgentOptions"/> for the Messages endpoint, which caches
    /// nothing unless asked, where the Responses API caches by itself. Two
    /// cache markers: one on the system prompt, so every turn reads the tools
    /// and the prompt from cache whatever the question, and the top-level one,
    /// which caches through the last block, so a turn's later round trips read
    /// its earlier ones. The prompt therefore travels here as a marked system
    /// block, not as <see cref="ChatOptions.Instructions"/>, which the adapter
    /// sends unmarked. Thinking is left at the model's default, as on the
    /// Responses path. Messages stores nothing, so there is no storage switch.
    /// </summary>
    public static ChatClientAgentOptions ClaudeAgentOptions(string instructions, string deployment, string? effort = null) =>
        BaseAgentOptions(null, _ => new MessageCreateParams
        {
            Model = deployment,
            MaxTokens = ClaudeMaxOutputTokens,
            Messages = [],
            System = new List<TextBlockParam>
            {
                new() { Text = instructions, CacheControl = new CacheControlEphemeral() },
            },
            CacheControl = new CacheControlEphemeral(),
            OutputConfig = effort is null ? null : new OutputConfig { Effort = ClaudeEffort(effort) },
        });

    /// <summary>An <see cref="AssistantOptions.EffortLevels"/> name as the SDK spells it.</summary>
    public static Effort ClaudeEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "not an effort level"),
    };

    private static ChatClientAgentOptions BaseAgentOptions(
        string? instructions, Func<IChatClient, object?> rawRepresentation) => new()
    {
        Name = "dotfit",
        // A round trip's calls run concurrently, so a program turn that looks
        // up five products waits for the slowest lookup rather than the sum.
        // The ledger, budget, meter and call records are safe under it.
        AllowConcurrentInvocation = true,
        ChatOptions = new ChatOptions
        {
            Instructions = instructions,
            RawRepresentationFactory = rawRepresentation,
        },
    };
#pragma warning restore OPENAI001

    /// <summary>The fully wired assistant: live Azure clients + the alias artifact.</summary>
    public static DotFitAssistant Create(
        RuntimeOptions options,
        AssistantOptions? assistantOptions = null,
        AliasTable? aliases = null,
        SearchSettings? settings = null,
        PriceSheet? prices = null,
        AssembledPrompt? prompt = null,
        SourceRegistry? registry = null)
    {
        aliases ??= AliasTable.Load(options.AliasTablePath);
        assistantOptions ??= AssistantOptions.Load(options.EnvFilePath);
        registry ??= SourceRegistry.Load(options.SourcesPath);
        prompt ??= AssembledPrompt.Load(options, assistantOptions, aliases, registry);
        prices ??= PriceSheet.Load(options.EnvFilePath);
        // Not `DefaultTop`: `top` is the model's per-call choice,
        // clamped against AssistantOptions, and SearchParameters always carries it
        // explicitly — so setting it here would describe a default nothing
        // reads. What this object does carry to the loop is the is_current
        // filter, the authority weights, the ranker switch and the candidate
        // pool, all of them read.
        settings ??= new SearchSettings();

        AzureOpenAIClient openAi = AssistantFactory.CreateOpenAiClient(options);
        SearchClient searchClient = AssistantFactory.CreateSearchClient(options);

        return new DotFitAssistant(
            agent: CreateAgent(options, openAi, prompt, assistantOptions.Effort),
            search: new AzureKnowledgeSearch(
                openAi, options.RequireEmbeddingDeployment(), searchClient, settings),
            store: new AzureDocumentStore(searchClient, settings),
            aliases: aliases,
            options: assistantOptions,
            supportContact: options.SupportContact,
            settings: settings,
            prices: prices,
            registry: registry);
    }
}
