using System.Text;
using Azure;
using DotFit.Agentic.Config;
using DotFit.Agentic.Prompting;
using DotFit.Agentic.Retrieval;
using DotFit.Agentic.Tools;
using DotFit.Agentic.Turn;
using DotFit.Agents;
using DotFit.Agents.Aliases;
using DotFit.Agents.Config;
using DotFit.Agents.Retrieval;

namespace DotFit.Agentic.Cli;

/// <summary>
/// <c>dotfit-agentic</c> — the harness the first owner sessions run against
/// (design §12.4). Everything real is in the library; this is wiring, argument
/// parsing and console rendering.
///
/// It renders the turn the way the widget will: stage lines while the model
/// works, the answer streaming live, the source list after. <c>--trace</c> adds
/// what the widget will never show — the exact tool arguments and the text each
/// call returned — because the first question about a bad answer is always
/// "what did it actually search for?"
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.WriteLine(CliArgs.Usage);
            return 0;
        }

        CliCommand command;
        try
        {
            command = CliArgs.Parse(args);
        }
        catch (CliUsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        try
        {
            return await RunAsync(command).ConfigureAwait(false);
        }
        catch (EnvFile.EnvFileException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (AliasTableException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (CliUsageException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (RequestFailedException e) { Console.Error.WriteLine($"azure request failed: {e.Message}"); return 1; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(CliCommand command)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected output may refuse */ }

        CliFlags flags = command.Flags;
        // `search` runs the tool alone, so it must not demand a chat deployment;
        // `prompt` needs nothing but the alias artifact.
        RuntimeNeeds needs = command.Verb switch
        {
            CliArgs.Search => RuntimeNeeds.Retrieval,
            CliArgs.Prompt => RuntimeNeeds.None,
            _ => AgenticFactory.Needs,
        };

        RuntimeOptions options = RuntimeOptions.Load(flags.EnvPath, needs: needs);
        if (flags.IndexName is not null)
            options = options with { IndexName = flags.IndexName };
        if (flags.AliasesPath is not null)
            options = options with { AliasTablePath = flags.AliasesPath };

        AliasTable aliases = AliasTable.Load(options.AliasTablePath);
        AgenticOptions agentic = AgenticOptions.Load(options.EnvFilePath);
        if (flags.Top is int top)
            agentic = agentic with { DefaultTop = top };

        return command.Verb switch
        {
            CliArgs.Prompt => Print(SystemPrompt.Build(aliases, options.SupportContact)),
            CliArgs.Config => Print($"{options}\n{agentic}\nalias table v{aliases.Version}, " +
                                    $"{aliases.Families.Count} families"),
            CliArgs.Search => await SearchAsync(options, aliases, agentic, command.Text).ConfigureAwait(false),
            CliArgs.Ask => await AskAsync(options, aliases, agentic, flags, command.Text).ConfigureAwait(false),
            CliArgs.Chat => await ChatAsync(options, aliases, agentic, flags).ConfigureAwait(false),
            CliArgs.Smoke => await Smoke.RunAsync(
                AgenticFactory.Create(options, agentic, aliases),
                flags.SmokeSet ?? DefaultPath(options, "runtime", "smoke", "conversations.jsonl"),
                flags.OutDir ?? DefaultPath(options, "processed", "agentic", "smoke"),
                flags.Tier).ConfigureAwait(false),
            _ => throw new CliUsageException($"unhandled verb '{command.Verb}'"),
        };
    }

    private static int Print(string text)
    {
        Console.Out.WriteLine(text);
        return 0;
    }

    /// <summary>
    /// Repo-relative default, anchored on the directory holding the <c>.env</c>
    /// — the same anchor <see cref="RuntimeOptions.AliasTablePath"/> uses. So
    /// `smoke` works from anywhere under the repo without a flag, and outputs
    /// land in the repo rather than in whatever directory it was run from.
    /// </summary>
    private static string DefaultPath(RuntimeOptions options, params string[] parts) =>
        Path.Combine([Path.GetDirectoryName(options.EnvFilePath) ?? ".", .. parts]);

    // ------------------------------------------------------------------ ask

    private static async Task<int> AskAsync(
        RuntimeOptions options, AliasTable aliases, AgenticOptions agentic, CliFlags flags, string question)
    {
        var assistant = AgenticFactory.Create(options, agentic, aliases);
        Console.Out.WriteLine(SystemPrompt.ConversationDisclosure());
        Console.Out.WriteLine();
        await RenderTurnAsync(assistant, new AskRequest { Question = question }, flags).ConfigureAwait(false);
        return 0;
    }

    // ----------------------------------------------------------------- chat

    private static async Task<int> ChatAsync(
        RuntimeOptions options, AliasTable aliases, AgenticOptions agentic, CliFlags flags)
    {
        var assistant = AgenticFactory.Create(options, agentic, aliases);
        var history = new List<ConversationTurn>();

        Console.Out.WriteLine(SystemPrompt.ConversationDisclosure());
        Console.Out.WriteLine();
        Console.Out.WriteLine("`reset` clears the conversation, `exit` quits.");
        Console.Out.WriteLine();

        while (true)
        {
            Console.Out.Write("> ");
            string? line = Console.In.ReadLine();
            if (line is null || line.Trim() is "exit" or "quit")
                return 0;

            string question = line.Trim();
            if (question.Length == 0)
                continue;
            if (question is "reset")
            {
                history.Clear();
                Console.Out.WriteLine("(conversation cleared)");
                Console.Out.WriteLine();
                continue;
            }

            TurnResult? result = await RenderTurnAsync(
                assistant,
                new AskRequest { Question = question, History = history },
                flags).ConfigureAwait(false);

            // The transcript the caller would keep: text only, no tool calls
            // and no sources — the same thing the service is sent (§6).
            history.Add(new ConversationTurn(ConversationRole.User, question));
            if (result is not null)
                history.Add(new ConversationTurn(ConversationRole.Assistant, result.AnswerText));
        }
    }

    /// <summary>
    /// One turn, rendered as the stream arrives. The ordering here is the
    /// contract under test: a source line is printed before any answer text
    /// that could cite it, because the library emits it that way (§7).
    /// </summary>
    private static async Task<TurnResult?> RenderTurnAsync(
        IAgenticAssistant assistant, AskRequest request, CliFlags flags)
    {
        TurnResult? result = null;
        bool answering = false;

        await foreach (TurnEvent turnEvent in assistant.AskAsync(request).ConfigureAwait(false))
        {
            switch (turnEvent)
            {
                case TurnStageEvent stage:
                    if (stage.Stage == Stages.Answer)
                        break;
                    Console.Out.WriteLine(stage.Detail is { Length: > 0 } detail
                        ? $"  ... {stage.Stage}: {detail}"
                        : $"  ... {stage.Stage}");
                    break;

                case TurnSourceEvent source when flags.Trace:
                    Console.Out.WriteLine(
                        $"      [{source.Source.N}] {source.Source.SourceType} " +
                        $"a{source.Source.Authority} {source.Source.Title}");
                    break;

                case TurnDeltaEvent delta:
                    if (!answering)
                    {
                        answering = true;
                        Console.Out.WriteLine();
                    }
                    Console.Out.Write(delta.Text);
                    break;

                case TurnErrorEvent error:
                    Console.Error.WriteLine($"  !!! {error.Kind}: {error.Message}");
                    break;

                case TurnResultEvent finished:
                    result = finished.Result;
                    break;
            }
        }

        Console.Out.WriteLine();
        if (result is null)
            return null;

        if (flags.Trace)
            RenderTrace(result);
        if (flags.ShowSources && result.Sources.Count > 0)
            RenderSources(result);

        Console.Out.WriteLine();
        Console.Out.WriteLine(
            $"  ({result.ToolCalls.Count} tool calls, {result.Sources.Count} sources, " +
            $"{result.CitedSources.Count} cited, first delta {result.FirstDeltaMs} ms, " +
            $"total {result.TotalMs} ms{(result.BudgetExhausted ? ", budget exhausted" : "")})");
        Console.Out.WriteLine();

        if (flags.Log)
            Console.Error.WriteLine(TurnLog.From(
                result,
                TurnLog.OutcomeAnswered,
                historyTurns: request.History.Count).ToJsonLine());

        return result;
    }

    private static void RenderTrace(TurnResult result)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("  tool calls:");
        foreach (ToolCallRecord call in result.ToolCalls)
        {
            Console.Out.WriteLine(
                $"    {call.Tool}({call.Argument}) -> {call.ResultCount} sources " +
                $"({call.NewSourceCount} new), {call.ElapsedMs} ms" +
                (call.Refusal is null ? "" : $" — {call.Refusal}"));
        }
    }

    private static void RenderSources(TurnResult result)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("  sources:");
        foreach (SourceRef source in result.Sources)
        {
            bool cited = result.CitedSources.Contains(source.N);
            Console.Out.WriteLine(
                $"  {(cited ? "*" : " ")}[{source.N}] {DotFit.Agents.Answering.Prompts.SourceKind(source.SourceType)}" +
                $" — {source.Title}" +
                (source.Locator is { Length: > 0 } loc ? $" ({loc})" : "") +
                (source.CitationUrl is { Length: > 0 } url ? $"\n         {url}" : ""));
        }
    }

    // --------------------------------------------------------------- search

    /// <summary>
    /// The search tool on its own, no model in the loop (design §12.2). This is
    /// what the retrieval probes run against and what makes a bad answer
    /// separable into "it retrieved the wrong thing" and "it read the right
    /// thing wrong".
    /// </summary>
    private static async Task<int> SearchAsync(
        RuntimeOptions options, AliasTable aliases, AgenticOptions agentic, string query)
    {
        var searchClient = RuntimeFactory.CreateSearchClient(options);
        var settings = new SearchSettings { DefaultTop = agentic.DefaultTop };
        var openAi = RuntimeFactory.CreateOpenAiClient(options);

        var tools = new KnowledgeTools(
            new AzureKnowledgeSearch(openAi, options.RequireEmbeddingDeployment(), searchClient, settings),
            new AzureDocumentStore(searchClient, settings),
            aliases,
            agentic,
            new SourceLedger(agentic.MaxSourceChars),
            new ToolBudget(agentic.MaxToolCalls, agentic.TurnTimeout));

        // Invoke through the same AIFunction the model would call, so what this
        // prints is exactly what the model would have been handed — including
        // the alias notes and the truncation markers.
        var function = (Microsoft.Extensions.AI.AIFunction)tools.AsTools()[0];
        object? result = await function.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["query"] = query }).ConfigureAwait(false);

        Console.Out.WriteLine(result?.ToString() ?? "(no result)");
        return 0;
    }
}
