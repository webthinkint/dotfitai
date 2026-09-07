using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using DotFit.Agents;
using DotFit.Agents.Aliases;
using DotFit.Agents.Config;
using DotFit.Agents.Guardrails;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Retrieval;

namespace DotFit.Agents.Cli;

/// <summary>
/// dotfit-agent — the testing/demo harness over the DotFit.Agents library
/// (plan §11 runtime). Everything real lives in the library; this is wiring,
/// argument parsing, and console rendering.
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
            return (int)await RunAsync(command).ConfigureAwait(false);
        }
        catch (EnvFile.EnvFileException e)         { Console.Error.WriteLine(e.Message); return 2; }
        catch (AliasTableException e)              { Console.Error.WriteLine(e.Message); return 2; }
        catch (CliUsageException e)                { Console.Error.WriteLine(e.Message); return 2; }
        catch (RequestFailedException e)           { Console.Error.WriteLine($"azure request failed: {e.Message}"); return 1; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static async Task<ExitCode> RunAsync(CliCommand command)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected output may refuse */ }

        var flags = command.Flags;
        // Load only the slice of the .env this verb actually uses: `search` must
        // not demand a chat deployment (open item 1 quota), and the single-stage
        // verbs need neither the search service nor the embedding deployment.
        RuntimeNeeds needs = command.Verb switch
        {
            CliArgs.Search => RuntimeNeeds.Retrieval,
            CliArgs.Guardrail or CliArgs.Rewrite => RuntimeNeeds.SmallChat,
            _ => RuntimeNeeds.Full,
        };
        RuntimeOptions options = RuntimeOptions.Load(flags.EnvPath, needs: needs);
        if (flags.IndexName is not null)
            options = options with { IndexName = flags.IndexName };
        if (flags.AliasesPath is not null)
            options = options with { AliasTablePath = flags.AliasesPath };

        return command.Verb switch
        {
            CliArgs.Ask => await Ask(options, flags, command.Text).ConfigureAwait(false),
            CliArgs.Chat => await Chat(options, flags).ConfigureAwait(false),
            CliArgs.Search => await Search(options, flags, command.Text).ConfigureAwait(false),
            CliArgs.Guardrail => await Guardrail(options, flags, command.Text).ConfigureAwait(false),
            CliArgs.Rewrite => await Rewrite(options, flags, command.Text).ConfigureAwait(false),
            _ => throw new CliUsageException(CliArgs.Usage),
        };
    }

    // --- ask / chat -----------------------------------------------------------

    private static async Task<ExitCode> Ask(
        RuntimeOptions options, CliFlags flags, string question)
    {
        if (!flags.Json)
            WriteBanner(interactive: false);
        KnowledgeAssistant assistant = RuntimeFactory.CreateAssistant(options);
        return await AskOnce(assistant, flags, question).ConfigureAwait(false);
    }

    private static async Task<ExitCode> Chat(RuntimeOptions options, CliFlags flags)
    {
        if (!flags.Json)
            WriteBanner(interactive: true);
        KnowledgeAssistant assistant = RuntimeFactory.CreateAssistant(options);
        while (true)
        {
            (flags.Json ? Console.Error : Console.Out).Write("\n> ");
            string? line = Console.ReadLine();
            if (line is null)
                break;
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (line is "exit" or "quit")
                break;
            await AskOnce(assistant, flags, line).ConfigureAwait(false);
        }
        return ExitCode.Ok;
    }

    private static async Task<ExitCode> AskOnce(
        KnowledgeAssistant assistant, CliFlags flags, string question)
    {
        AnswerStreamMode mode = flags.Gated ? AnswerStreamMode.Gated : AnswerStreamMode.Live;
        var askOptions = new AskOptions
        {
            ClaimsCheck = !flags.NoClaimsCheck,
            Top = flags.Top,
            Semantic = flags.Semantic,
            Filter = flags.Filter,
            StreamMode = mode,
        };

        // --json owns stdout: the eval harness parses it whole, so every human
        // rendering below is suppressed and the trace goes to stderr instead.
        AssistantResult? result = null;
        await foreach (AssistantEvent e in assistant.AskStreamAsync(question, askOptions).ConfigureAwait(false))
        {
            switch (e)
            {
                case StageEvent s when flags.Trace:
                    (flags.Json ? Console.Error : Console.Out).WriteLine(Dim($"· {s.Stage,-10} {s.Detail}"));
                    break;
                case DeltaEvent d when !flags.NoStream && !flags.Json:
                    Console.Write(d.Text);
                    break;
                case RetractionEvent x when !flags.Json:
                    Console.WriteLine();
                    Console.WriteLine(Dim(x.Mode == AnswerStreamMode.Gated
                        ? $"⚠ answer withheld before delivery: {x.Reason}"
                        : $"⚠ answer already delivered — retract: {x.Reason}"));
                    break;
                case ResultEvent r:
                    result = r.Result;
                    break;
            }
        }
        if (result is null)
            throw new InvalidOperationException("the pipeline ended without a result");

        if (flags.Json)
        {
            Console.WriteLine(AskJson.Serialize(result, mode));
            return result.PostCheck.Passed ? ExitCode.Ok : ExitCode.PostCheckFailed;
        }

        Console.WriteLine();
        if (flags.NoStream)
            Console.WriteLine(result.DeliveredText);
        if (result.Citations.Count > 0 && !result.Withheld)
        {
            Console.WriteLine();
            Console.WriteLine(result.RenderedCitations);
        }
        Console.WriteLine(Dim(PostCheckLine(result)));
        if (result.Withheld && flags.Trace)   // the harness still wants to read what failed
            Console.WriteLine(Dim($"withheld draft:\n{result.AnswerText}"));
        if (flags.Trace)
            Console.WriteLine(Dim(string.Join(" · ",
                result.StageSeconds.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.00}s"))));
        return result.PostCheck.Passed ? ExitCode.Ok : ExitCode.PostCheckFailed;
    }

    private static string PostCheckLine(AssistantResult result)
    {
        var sb = new StringBuilder("post-check: ").Append(result.PostCheck.Passed ? "PASS" : "FAIL");
        foreach (string failure in result.PostCheck.Failures)
            sb.Append("\n  ✗ ").Append(failure);
        foreach (string warning in result.PostCheck.Warnings)
            sb.Append("\n  ⚠ ").Append(warning);
        if (result.PostCheck.Claims is { } claims)
        {
            sb.Append(claims.Degraded
                ? "\n  claims: check unavailable"
                : claims.Compliant ? "\n  claims: compliant" : "\n  claims: NON-COMPLIANT");
            foreach (string violation in claims.Violations)
                sb.Append("\n    ✗ ").Append(violation);
        }
        return sb.ToString();
    }

    // --- search -----------------------------------------------------------------

    private static async Task<ExitCode> Search(RuntimeOptions options, CliFlags flags, string query)
    {
        string queryText = query;
        if (!flags.Raw)
        {
            AliasTable aliases = AliasTable.Load(options.AliasTablePath);
            AliasExpansion expansion = aliases.Expand(query);
            if (expansion.SearchTerms.Count > 0)
                queryText = string.Join(" ", query, string.Join(" ", expansion.SearchTerms.Distinct()));
            if (flags.Trace && expansion.Families.Count > 0)
                Console.WriteLine(Dim($"· aliases    families: {string.Join(", ", expansion.Families)}"));
        }

        var settings = new SearchSettings();
        IKnowledgeSearch search = RuntimeFactory.CreateSearch(options, settings);
        var parameters = new SearchParameters
        {
            QueryText = queryText,
            Top = flags.Top ?? settings.DefaultTop,
            VectorCandidates = settings.VectorCandidates,
            Semantic = flags.Semantic ?? settings.SemanticDefault,
            AdditionalFilter = flags.Filter,
        };
        IReadOnlyList<RetrievedDocument> docs = await search.SearchAsync(parameters).ConfigureAwait(false);

        if (flags.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(docs.Select(d => new
            {
                d.Id, d.SourceType, d.Authority, d.Title,
                score = Math.Round(d.Score, 5),
                boosted = Math.Round(d.BoostedScore, 5),
                reranker = d.RerankerScore is { } r ? Math.Round(r, 3) : (double?)null,
                d.CitationUrl, d.Locator, d.Products,
            }), JsonOptions));
        }
        else
        {
            foreach (var (doc, rank) in docs.Select((d, i) => (d, i + 1)))
            {
                string link = doc.CitationUrl ?? doc.Locator ?? "";
                Console.WriteLine(
                    $"{rank,2}. {doc.BoostedScore,7:0.00000} (raw {doc.Score:0.00000}) · " +
                    $"{SourceTypeTag(doc.SourceType)} · auth {doc.Authority} · {doc.Title}" +
                    (link.Length > 0 ? $" · {link}" : ""));
                Console.WriteLine(Dim($"   {Snippet(doc.Content, 150)}"));
            }
        }
        return ExitCode.Ok;
    }

    // --- guardrail / rewrite ------------------------------------------------------

    private static async Task<ExitCode> Guardrail(RuntimeOptions options, CliFlags flags, string question)
    {
        Azure.AI.OpenAI.AzureOpenAIClient client = RuntimeFactory.CreateOpenAiClient(options);
        var guardrail = new AgentGuardrail(RuntimeFactory.CreateGuardrailAgent(options, client));
        GuardrailVerdict verdict = await guardrail.CheckAsync(question).ConfigureAwait(false);

        if (flags.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(verdict, JsonOptions));
            return ExitCode.Ok;
        }
        Console.WriteLine($"escalate:   {verdict.Escalate}");
        if (verdict.Reasons.Count > 0)
            Console.WriteLine($"reasons:    {string.Join(", ", verdict.Reasons)}");
        Console.WriteLine($"claim_trap: {verdict.ClaimTrap}");
        Console.WriteLine($"notes:      {verdict.Notes}");
        if (verdict.Degraded)
            Console.WriteLine(Dim("degraded:   yes — the check could not run (fail-open by design)"));
        return ExitCode.Ok;
    }

    private static async Task<ExitCode> Rewrite(RuntimeOptions options, CliFlags flags, string question)
    {
        AliasTable aliases = AliasTable.Load(options.AliasTablePath);
        Azure.AI.OpenAI.AzureOpenAIClient client = RuntimeFactory.CreateOpenAiClient(options);
        var rewriter = new AgentQueryRewriter(
            RuntimeFactory.CreateRewriteAgent(options, client),
            aliases.Families.Select(f => f.Family).OrderBy(f => f, StringComparer.Ordinal).ToList());
        RewriteResult rewrite = await rewriter.RewriteAsync(question).ConfigureAwait(false);
        AliasExpansion expansion = aliases.Expand(question, rewrite.ProductMentions);

        if (flags.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                rewrite.CanonicalQuestion, rewrite.ProductMentions, rewrite.Topics,
                rewrite.Confidence, rewrite.Degraded,
                families = expansion.Families, part_nos = expansion.PartNos,
                notes = expansion.Notes,
            }, JsonOptions));
            return ExitCode.Ok;
        }
        Console.WriteLine($"canonical: {rewrite.CanonicalQuestion}");
        Console.WriteLine($"mentions:  {(rewrite.ProductMentions.Count > 0 ? string.Join(", ", rewrite.ProductMentions) : "—")}");
        Console.WriteLine($"topics:    {(rewrite.Topics.Count > 0 ? string.Join(", ", rewrite.Topics) : "—")}");
        Console.WriteLine($"confidence: {rewrite.Confidence:0.00}");
        Console.WriteLine($"families:  {(expansion.Families.Count > 0 ? string.Join(", ", expansion.Families) : "—")}");
        if (expansion.PartNos.Count > 0)
            Console.WriteLine($"part_nos:  {string.Join(",", expansion.PartNos)}");
        foreach (string note in expansion.Notes)
            Console.WriteLine(Dim($"note: {note}"));
        if (rewrite.Degraded)
            Console.WriteLine(Dim($"degraded:  yes — {rewrite.DegradedReason}"));
        return ExitCode.Ok;
    }

    // --- rendering helpers ----------------------------------------------------------

    private enum ExitCode
    {
        Ok = 0,
        PostCheckFailed = 1,
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly bool UseColor =
        !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Dim(string text) => UseColor ? $"\x1b[2m{text}\x1b[0m" : text;

    private static void WriteBanner(bool interactive)
    {
        Console.WriteLine("dotFIT knowledge assistant (AI) — nutrition guidance, not medical advice.");
        Console.WriteLine(Dim(
            "Grounded in dotFIT approved product copy, the practitioner reference guide, " +
            "customer Q&A, podcasts and menus; sources cited as [n]."));
        if (interactive)
            Console.WriteLine(Dim("Type a question, or 'exit' to quit."));
    }

    private static string SourceTypeTag(string sourceType) => sourceType switch
    {
        "product" => "product",
        "pdsrg" => "pdsrg",
        "qa" => "qa",
        "podcast" => "podcast",
        "menu_desc" => "menu",
        _ => sourceType,
    };

    private static string Snippet(string content, int max)
    {
        string one = content.ReplaceLineEndings(" ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
