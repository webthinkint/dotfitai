using System.ClientModel;
using Azure;
using DotFit.Assistant.Cost;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using DotFit.Assistant.Config;
using DotFit.Assistant.Prompting;
using DotFit.Assistant.Retrieval;
using DotFit.Assistant;
using DotFit.Assistant.Aliases;
using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace DotFit.Assistant;

/// <summary>
/// Wires <see cref="RuntimeOptions"/> and <see cref="AssistantOptions"/> into a
/// live assistant. All the behavior is in the components; this
/// only builds Azure clients and the agent over them.
///
/// One chat deployment runs the loop (<c>AZURE_OPENAI_CHAT_DEPLOYMENT</c>) and
/// <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> embeds queries; nothing else calls
/// a model.
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
    public static AIAgent CreateAgent(RuntimeOptions options, AzureOpenAIClient client, AssembledPrompt prompt) =>
        client.GetChatClient(options.RequireChatDeployment())
            .AsAIAgent(
                name: "dotfit",
                instructions: prompt.Text);

    /// <summary>The fully wired assistant: live Azure clients + the §5 alias artifact.</summary>
    public static DotFitAssistant Create(
        RuntimeOptions options,
        AssistantOptions? agentic = null,
        AliasTable? aliases = null,
        SearchSettings? settings = null,
        PriceSheet? prices = null,
        AssembledPrompt? prompt = null)
    {
        aliases ??= AliasTable.Load(options.AliasTablePath);
        agentic ??= AssistantOptions.Load(options.EnvFilePath);
        prompt ??= AssembledPrompt.Load(options, agentic, aliases);
        prices ??= PriceSheet.Load(options.EnvFilePath);
        // Not `DefaultTop`: on this branch `top` is the model's per-call choice,
        // clamped against AssistantOptions, and SearchParameters always carries it
        // explicitly — so setting it here would describe a default nothing
        // reads. What this object does carry to the loop is the is_current
        // filter, the authority weights, the ranker switch and the candidate
        // pool, all of them read.
        settings ??= new SearchSettings();

        AzureOpenAIClient openAi = AssistantFactory.CreateOpenAiClient(options);
        SearchClient searchClient = AssistantFactory.CreateSearchClient(options);

        return new DotFitAssistant(
            agent: CreateAgent(options, openAi, prompt),
            search: new AzureKnowledgeSearch(
                openAi, options.RequireEmbeddingDeployment(), searchClient, settings),
            store: new AzureDocumentStore(searchClient, settings),
            aliases: aliases,
            options: agentic,
            supportContact: options.SupportContact,
            settings: settings,
            prices: prices);
    }
}
