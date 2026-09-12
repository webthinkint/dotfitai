using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotFit.Agentic.Turn;
using DotFit.Agents;

namespace DotFit.Agentic.Cli;

/// <summary>One item of the written conversational smoke set (design §11.2).</summary>
internal sealed record SmokeItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("tier")] public string Tier { get; init; } = "";
    /// <summary>Sequential user turns. History accumulates across them.</summary>
    [JsonPropertyName("turns")] public string[] Turns { get; init; } = [];
    /// <summary>What a human should check. There is no rubric and no score.</summary>
    [JsonPropertyName("looking_for")] public string LookingFor { get; init; } = "";
}

/// <summary>
/// Runs the smoke set live and writes a transcript a non-engineer can read
/// (design §11.2, build order §12.7).
///
/// It deliberately reports **no pass rate**. The set has no labels and no
/// expected answers, so a number computed from it would be invented — and this
/// branch's whole evaluation position (decision D7) is that an unvalidated
/// number is worse than an honest transcript. What it does report is the three
/// things that can be read mechanically: latency, whether tools were called,
/// and whether anything was cited.
///
/// It costs real Azure calls — one loop per turn, and the multi-turn items run
/// each of their turns.
/// </summary>
internal static class Smoke
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<SmokeItem> Load(string path)
    {
        if (!File.Exists(path))
            throw new CliUsageException(
                $"smoke set not found at {path} — it lives in runtime/smoke/conversations.jsonl, " +
                "or pass --set <path>");

        var items = new List<SmokeItem>();
        int lineNo = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            lineNo++;
            if (line.Trim().Length == 0)
                continue;
            SmokeItem item;
            try
            {
                item = JsonSerializer.Deserialize<SmokeItem>(line, Json)
                    ?? throw new CliUsageException($"{path} line {lineNo}: null item");
            }
            catch (JsonException e)
            {
                throw new CliUsageException($"{path} line {lineNo}: {e.Message}");
            }
            if (item.Id.Length == 0 || item.Turns.Length == 0)
                throw new CliUsageException($"{path} line {lineNo}: an item needs an id and at least one turn");
            items.Add(item);
        }
        return items;
    }

    public static async Task<int> RunAsync(
        IAgenticAssistant assistant, string setPath, string outDir, string? tier)
    {
        IReadOnlyList<SmokeItem> items = Load(setPath);
        if (tier is not null)
            items = [.. items.Where(i => string.Equals(i.Tier, tier, StringComparison.OrdinalIgnoreCase))];
        if (items.Count == 0)
            throw new CliUsageException($"no items matched tier '{tier}'");

        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
        string path = Path.Combine(outDir, $"smoke-{stamp}.md");

        var doc = new StringBuilder();
        doc.AppendLine($"# Agentic smoke run — {DateTime.Now:yyyy-MM-dd HH:mm}");
        doc.AppendLine();
        doc.AppendLine(
            "Written conversational set (design §11.2). **There is no score.** Read each answer " +
            "against *Looking for*, and check the three mechanical things first: first-delta time, " +
            "whether tools were called, and whether anything was cited.");
        doc.AppendLine();

        var summary = new List<string>();

        foreach (SmokeItem item in items)
        {
            Console.Out.WriteLine($"{item.Id} ({item.Tier}) — {item.Turns.Length} turn(s)");
            doc.AppendLine($"## {item.Id} — {item.Tier}");
            doc.AppendLine();
            doc.AppendLine($"*Looking for:* {item.LookingFor}");
            doc.AppendLine();

            var history = new List<ConversationTurn>();
            foreach (string question in item.Turns)
            {
                TurnResult? result = await OneTurnAsync(assistant, question, history, doc).ConfigureAwait(false);
                history.Add(new ConversationTurn(ConversationRole.User, question));
                if (result is null)
                    break;
                history.Add(new ConversationTurn(ConversationRole.Assistant, result.AnswerText));
                summary.Add(
                    $"| {item.Id} | {item.Tier} | {result.ToolCalls.Count} | {result.Sources.Count} | " +
                    $"{result.CitedSources.Count} | {result.FirstDeltaMs} | {result.TotalMs} |");
            }
            doc.AppendLine("---");
            doc.AppendLine();
        }

        doc.AppendLine("## At a glance");
        doc.AppendLine();
        doc.AppendLine("| id | tier | tools | sources | cited | first delta (ms) | total (ms) |");
        doc.AppendLine("|---|---|---|---|---|---|---|");
        foreach (string row in summary)
            doc.AppendLine(row);

        await File.WriteAllTextAsync(path, doc.ToString()).ConfigureAwait(false);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"transcript written to {path}");
        Console.Out.WriteLine("Read it. There is no pass rate on purpose (design §11).");
        return 0;
    }

    private static async Task<TurnResult?> OneTurnAsync(
        IAgenticAssistant assistant, string question, List<ConversationTurn> history, StringBuilder doc)
    {
        doc.AppendLine($"**Q:** {question}");
        doc.AppendLine();

        var stages = new List<string>();
        var answer = new StringBuilder();
        TurnResult? result = null;
        string? error = null;

        await foreach (TurnEvent turnEvent in assistant
            .AskAsync(new AskRequest { Question = question, History = history })
            .ConfigureAwait(false))
        {
            switch (turnEvent)
            {
                case TurnStageEvent stage when stage.Stage != Stages.Thinking && stage.Stage != Stages.Answer:
                    stages.Add(stage.Detail is { Length: > 0 } d ? $"{stage.Stage}: {d}" : stage.Stage);
                    break;
                case TurnDeltaEvent delta:
                    answer.Append(delta.Text);
                    break;
                case TurnErrorEvent failure:
                    error = $"{failure.Kind}: {failure.Message}";
                    break;
                case TurnResultEvent finished:
                    result = finished.Result;
                    break;
            }
        }

        if (stages.Count > 0)
        {
            doc.AppendLine("*What it did:*");
            foreach (string stage in stages)
                doc.AppendLine($"- `{stage}`");
            doc.AppendLine();
        }
        else
        {
            doc.AppendLine("*What it did:* nothing — answered without a lookup.");
            doc.AppendLine();
        }

        if (error is not null)
            doc.AppendLine($"> **ERROR** — {error}").AppendLine();

        doc.AppendLine("**A:**");
        doc.AppendLine();
        doc.AppendLine(answer.Length > 0 ? answer.ToString().Trim() : "*(nothing)*");
        doc.AppendLine();

        if (result is not null)
        {
            if (result.Sources.Count > 0)
            {
                doc.AppendLine("*Sources* (★ = cited):");
                foreach (SourceRef source in result.Sources)
                {
                    doc.AppendLine(
                        $"- {(result.CitedSources.Contains(source.N) ? "★" : " ")} `[{source.N}]` " +
                        $"{DotFit.Agents.Answering.Prompts.SourceKind(source.SourceType)} " +
                        $"(authority {source.Authority}{(source.Quotable ? ", quotable" : ", context only")}) — " +
                        $"{source.Title}");
                }
                doc.AppendLine();
            }
            doc.AppendLine(
                $"*{result.ToolCalls.Count} tool calls, {result.Sources.Count} sources, " +
                $"{result.CitedSources.Count} cited, first delta {result.FirstDeltaMs} ms, " +
                $"total {result.TotalMs} ms" +
                $"{(result.BudgetExhausted ? ", **budget exhausted**" : "")}*");
            doc.AppendLine();
        }

        return result;
    }
}
