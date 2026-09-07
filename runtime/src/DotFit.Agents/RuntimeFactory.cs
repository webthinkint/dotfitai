using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using DotFit.Agents.Aliases;
using DotFit.Agents.Answering;
using DotFit.Agents.Config;
using DotFit.Agents.Guardrails;
using DotFit.Agents.PostCheck;
using DotFit.Agents.Retrieval;
using DotFit.Agents.Rewrite;
using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace DotFit.Agents;

/// <summary>
/// Wires <see cref="RuntimeOptions"/> into the live pipeline pieces. All the
/// interesting behavior lives in the components; this factory only builds
/// Azure clients (with the api-version pin) and agents over them.
/// </summary>
public static class RuntimeFactory
{
    /// <summary>
    /// The api-version the pipeline smokes verified live on the Foundry v2
    /// resource (chat_smoke.py / embedding smoke). The stable Azure.AI.OpenAI
    /// GA build only offers 2024-10-21, which the v2 endpoint 404s — hence the
    /// prerelease package pin in the csproj.
    /// </summary>
    public const AzureOpenAIClientOptions.ServiceVersion OpenAiServiceVersion =
        AzureOpenAIClientOptions.ServiceVersion.V2025_04_01_Preview;

    public static AzureOpenAIClient CreateOpenAiClient(RuntimeOptions options) => new(
        options.OpenAiEndpoint,
        new ApiKeyCredential(options.OpenAiApiKey),
        new AzureOpenAIClientOptions(OpenAiServiceVersion));

    public static SearchClient CreateSearchClient(RuntimeOptions options) => new(
        options.SearchEndpoint, options.IndexName, new AzureKeyCredential(options.SearchKey));

    public static AIAgent CreateGuardrailAgent(RuntimeOptions options, AzureOpenAIClient client) =>
        client.GetChatClient(options.SmallChatDeployment)
            .AsAIAgent(name: "dotfit-guardrail", instructions: Prompts.GuardrailInstructions);

    public static AIAgent CreateRewriteAgent(RuntimeOptions options, AzureOpenAIClient client) =>
        client.GetChatClient(options.SmallChatDeployment)
            .AsAIAgent(name: "dotfit-rewriter", instructions: Prompts.RewriteInstructions);

    public static AIAgent CreateClaimsAgent(RuntimeOptions options, AzureOpenAIClient client) =>
        client.GetChatClient(options.SmallChatDeployment)
            .AsAIAgent(name: "dotfit-claims", instructions: Prompts.ClaimsInstructions);

    public static AIAgent CreateAnswerAgent(RuntimeOptions options, AzureOpenAIClient client) =>
        client.GetChatClient(options.ChatDeployment)
            .AsAIAgent(name: "dotfit-assistant", instructions: Prompts.AnswerInstructions);

    /// <summary>The fully wired assistant: live Azure clients + the §5 alias artifact.</summary>
    public static KnowledgeAssistant CreateAssistant(
        RuntimeOptions options, AliasTable? aliases = null, SearchSettings? settings = null)
    {
        aliases ??= AliasTable.Load(options.AliasTablePath);
        settings ??= new SearchSettings();
        AzureOpenAIClient openAi = CreateOpenAiClient(options);
        return new KnowledgeAssistant(
            guardrail: new AgentGuardrail(CreateGuardrailAgent(options, openAi)),
            rewriter: new AgentQueryRewriter(CreateRewriteAgent(options, openAi), FamilyNames(aliases)),
            aliases: aliases,
            search: new AzureKnowledgeSearch(openAi, options.EmbeddingDeployment,
                CreateSearchClient(options), settings),
            answer: new AgentAnswerAgent(CreateAnswerAgent(options, openAi)),
            settings: settings,
            claimsChecker: new AgentClaimsLanguageChecker(CreateClaimsAgent(options, openAi)));
    }

    /// <summary>Retrieval-only wiring for the search command (no chat deployments needed).</summary>
    public static IKnowledgeSearch CreateSearch(RuntimeOptions options, SearchSettings? settings = null)
    {
        settings ??= new SearchSettings();
        return new AzureKnowledgeSearch(
            CreateOpenAiClient(options), options.EmbeddingDeployment, CreateSearchClient(options), settings);
    }

    private static IReadOnlyList<string> FamilyNames(AliasTable aliases) =>
        aliases.Families.Select(f => f.Family).OrderBy(f => f, StringComparer.Ordinal).ToList();
}
