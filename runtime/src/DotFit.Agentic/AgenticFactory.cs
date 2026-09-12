using Azure.AI.OpenAI;
using Azure.Search.Documents;
using DotFit.Agentic.Config;
using DotFit.Agentic.Prompting;
using DotFit.Agentic.Retrieval;
using DotFit.Agents;
using DotFit.Agents.Aliases;
using DotFit.Agents.Config;
using DotFit.Agents.Retrieval;
using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace DotFit.Agentic;

/// <summary>
/// Wires <see cref="RuntimeOptions"/> and <see cref="AgenticOptions"/> into a
/// live assistant (design §12.1). All the behavior is in the components; this
/// only builds Azure clients and the agent over them.
///
/// One deployment does everything (decision D2): the frontier
/// <c>AZURE_OPENAI_CHAT_DEPLOYMENT</c> runs the loop and
/// <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> embeds queries. The small-chat
/// deployment is deliberately unused — no stage on this branch calls a second
/// model — so this project asks for <see cref="RuntimeNeeds.Search"/> +
/// <see cref="RuntimeNeeds.Embedding"/> + <see cref="RuntimeNeeds.Chat"/> and
/// boots fine on a resource that has no small deployment at all.
/// </summary>
public static class AgenticFactory
{
    /// <summary>What the loop needs out of the <c>.env</c> contract. Notably not SmallChat.</summary>
    public const RuntimeNeeds Needs = RuntimeNeeds.Search | RuntimeNeeds.Embedding | RuntimeNeeds.Chat;

    /// <summary>
    /// The agent: the frontier deployment, carrying the assembled system
    /// prompt as its instructions. Tools are *not* bound here — they are
    /// turn-scoped (their ledger and budget are) and arrive as per-run options.
    /// </summary>
    public static AIAgent CreateAgent(RuntimeOptions options, AzureOpenAIClient client, AliasTable aliases) =>
        client.GetChatClient(options.RequireChatDeployment())
            .AsAIAgent(
                name: "dotfit-agentic",
                instructions: SystemPrompt.Build(aliases, options.SupportContact));

    /// <summary>The fully wired assistant: live Azure clients + the §5 alias artifact.</summary>
    public static AgenticAssistant Create(
        RuntimeOptions options,
        AgenticOptions? agentic = null,
        AliasTable? aliases = null,
        SearchSettings? settings = null)
    {
        aliases ??= AliasTable.Load(options.AliasTablePath);
        agentic ??= AgenticOptions.Load(options.EnvFilePath);
        settings ??= new SearchSettings { DefaultTop = agentic.DefaultTop };

        AzureOpenAIClient openAi = RuntimeFactory.CreateOpenAiClient(options);
        SearchClient searchClient = RuntimeFactory.CreateSearchClient(options);

        return new AgenticAssistant(
            agent: CreateAgent(options, openAi, aliases),
            search: new AzureKnowledgeSearch(
                openAi, options.RequireEmbeddingDeployment(), searchClient, settings),
            store: new AzureDocumentStore(searchClient, settings),
            aliases: aliases,
            options: agentic,
            supportContact: options.SupportContact);
    }
}
