using System.Text.Json;
using DotFit.Agents;

namespace DotFit.Agents.Cli;

public sealed record CliFlags
{
    public string? EnvPath { get; init; }
    public string? AliasesPath { get; init; }
    public string? IndexName { get; init; }
    public bool Trace { get; init; }
    public bool NoStream { get; init; }
    public bool NoClaimsCheck { get; init; }
    /// <summary>ask/chat: hold the answer until the post-check passes (the service default).</summary>
    public bool Gated { get; init; }
    public int? Top { get; init; }
    public bool? Semantic { get; init; }
    public string? Filter { get; init; }
    public bool Json { get; init; }
    /// <summary>search command: skip alias expansion (raw text search).</summary>
    public bool Raw { get; init; }

    /// <summary>
    /// ask/guardrail: earlier turns of the conversation, parsed from the
    /// <c>--history</c> JSON. Empty is a standalone question — every use of
    /// this CLI except the §12 multi-turn set, which is the reason the flag
    /// exists: escalation over a conversation cannot be measured by a harness
    /// that can only send one turn (open item 19).
    /// </summary>
    public IReadOnlyList<ConversationTurn> History { get; init; } = ConversationHistory.Empty;
}

public sealed record CliCommand(string Verb, string Text, CliFlags Flags);

public sealed class CliUsageException(string message) : Exception(message);

/// <summary>Hand-rolled argv parsing — five commands, a dozen flags, no dependencies.</summary>
public static class CliArgs
{
    public const string Ask = "ask";
    public const string Chat = "chat";
    public const string Search = "search";
    public const string Guardrail = "guardrail";
    public const string Rewrite = "rewrite";
    public const string Help = "help";

    public const string Usage = """
        dotfit-agent — dotFIT knowledge assistant runtime (plan §11)

        Usage:
          dotfit-agent ask <question> [flags]      full pipeline: guardrail → rewrite →
                                                   search → grounded answer → post-check
          dotfit-agent chat [flags]                interactive, multi-turn: follow-ups are
                                                   resolved against the session ('reset' clears it)
          dotfit-agent search <query> [flags]      retrieval only (no LLM answer)
          dotfit-agent guardrail <question>        guardrail verdict only
          dotfit-agent rewrite <question>          rewrite + alias expansion only

        Flags:
          --env <path>          .env file (default: walk up from the current directory)
          --aliases <path>      alias table JSON (default: <env dir>/processed/aliases/alias_table.json)
          --index <name>        AI Search index (default kb-main-v2)
          --top <n>             sources fed to the answer (default 8)
          --semantic|--no-semantic   semantic ranker on/off (default off; open item 5)
          --filter <odata>      extra OData filter, ANDed with is_current eq true
          --trace               print per-stage trace lines
          --no-stream           print the answer only once complete
          --no-claims-check     skip the claims-language post-check
          --gated               hold the answer until the post-check passes, as the
                                SSE service does (default here: stream live, so a
                                failed check is visible only after the fact)
          --history <json>      ask/guardrail: earlier turns of this conversation as a
                                JSON array, oldest first and excluding the question
                                being asked. Same shape as POST /ask takes:
                                [{"role":"user","text":"I am 14"}]. This is how the
                                §12 multi-turn set is driven; `chat` keeps its own
                                transcript instead
          --raw                 search: skip alias expansion
          --json                machine-readable output. On ask/chat this is the
                                §12 eval-harness contract: one JSON object per
                                question on stdout (both answer texts, sources
                                with content, post-check, claims verdict), with
                                the banner and any --trace lines on stderr
          -h, --help            this help

        The question is the free text after the flags — quotes optional:
          dotfit-agent ask can I take creatine with my morning coffee

        Exit codes: 0 ok · 1 failed post-check or runtime error · 2 usage/config
        """;

    private static readonly string[] Verbs = [Ask, Chat, Search, Guardrail, Rewrite, Help];

    public static CliCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            throw new CliUsageException(Usage);

        string verb = args[0].ToLowerInvariant();
        if (!Verbs.Contains(verb))
            throw new CliUsageException($"unknown command '{args[0]}'\n\n{Usage}");

        var textParts = new List<string>();
        string? env = null, aliases = null, index = null, filter = null, historyJson = null;
        int? top = null;
        bool trace = false, noStream = false, noClaims = false, json = false, raw = false;
        bool gated = false;
        bool? semantic = null;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                    throw new CliUsageException($"flag {a} needs a value\n\n{Usage}");
                return args[++i];
            }
            switch (a)
            {
                case "--trace": trace = true; break;
                case "--no-stream": noStream = true; break;
                case "--no-claims-check": noClaims = true; break;
                case "--gated": gated = true; break;
                case "--json": json = true; break;
                case "--raw": raw = true; break;
                case "--semantic": semantic = true; break;
                case "--no-semantic": semantic = false; break;
                case "--env": env = Value(); break;
                case "--aliases": aliases = Value(); break;
                case "--index": index = Value(); break;
                case "--filter": filter = Value(); break;
                case "--history": historyJson = Value(); break;
                case "--top":
                    if (!int.TryParse(Value(), out int n) || n < 1)
                        throw new CliUsageException("--top must be a positive integer");
                    top = n;
                    break;
                default:
                    if (a.StartsWith('-') && a != "-")
                        throw new CliUsageException($"unknown flag '{a}'\n\n{Usage}");
                    textParts.Add(a);
                    break;
            }
        }

        string text = string.Join(" ", textParts).Trim();
        if (verb is not (Chat or Help) && text.Length == 0)
            throw new CliUsageException($"{verb} needs a question\n\n{Usage}");

        return new CliCommand(verb, text, new CliFlags
        {
            EnvPath = env, AliasesPath = aliases, IndexName = index,
            Trace = trace, NoStream = noStream, NoClaimsCheck = noClaims, Gated = gated,
            Top = top, Semantic = semantic, Filter = filter, Json = json, Raw = raw,
            History = ParseHistory(historyJson),
        });
    }

    /// <summary>
    /// <c>--history</c> JSON → turns. The wire shape is the SSE service's, so
    /// one transcript drives either surface, and the role vocabulary is
    /// <see cref="ConversationHistory.TryParseRole"/>'s — an unknown role is a
    /// usage error here for the same reason it is a 400 there: a dropped turn
    /// silently changes what the conversation said.
    /// </summary>
    private static IReadOnlyList<ConversationTurn> ParseHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ConversationHistory.Empty;

        List<HistoryTurnJson>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<HistoryTurnJson>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException e)
        {
            throw new CliUsageException($"--history is not valid JSON: {e.Message}");
        }
        if (raw is null)
            return ConversationHistory.Empty;

        var turns = new List<ConversationTurn>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            if (!ConversationHistory.TryParseRole(raw[i].Role, out ConversationRole role))
                throw new CliUsageException(
                    $"--history[{i}].role must be \"user\" or \"assistant\"");
            turns.Add(new ConversationTurn(role, raw[i].Text ?? ""));
        }
        return turns;
    }

    private sealed record HistoryTurnJson
    {
        public string? Role { get; init; }
        public string? Text { get; init; }
    }
}
