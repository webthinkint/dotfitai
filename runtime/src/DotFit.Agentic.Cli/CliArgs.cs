namespace DotFit.Agentic.Cli;

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed record CliFlags
{
    public string? EnvPath { get; init; }
    public string? IndexName { get; init; }
    public string? AliasesPath { get; init; }
    public int? Top { get; init; }
    /// <summary>Show every tool call, its arguments and what it returned.</summary>
    public bool Trace { get; init; }
    /// <summary>Emit the §10 turn-log line to stderr after each turn.</summary>
    public bool Log { get; init; }
    /// <summary>Print the sources the answer was given, after the answer.</summary>
    public bool ShowSources { get; init; } = true;

    /// <summary>`smoke`: where the written set lives, and where transcripts go.</summary>
    public string? SmokeSet { get; init; }
    public string? OutDir { get; init; }
    public string? Tier { get; init; }
}

internal sealed record CliCommand(string Verb, string Text, CliFlags Flags);

/// <summary>
/// Argument parsing for <c>dotfit-agentic</c> (design §12.4). Deliberately its
/// own small parser rather than a shared one with v1's CLI: the two harnesses
/// drive different runtimes and their flags have already diverged — v1 has
/// <c>--semantic</c>, <c>--mode</c> and <c>--no-claims</c>, none of which mean
/// anything here.
/// </summary>
internal static class CliArgs
{
    public const string Ask = "ask";
    public const string Chat = "chat";
    public const string Search = "search";
    public const string Prompt = "prompt";
    public const string Config = "config";
    public const string Smoke = "smoke";

    public const string Usage = """
        dotfit-agentic — the agentic knowledge assistant (design §6).

        USAGE
          dotfit-agentic ask "<question>"     one question, streamed
          dotfit-agentic chat                 interactive; the session keeps history
          dotfit-agentic search "<query>"     run the search tool alone, no model
          dotfit-agentic prompt               print the assembled system prompt
          dotfit-agentic config               print the resolved configuration
          dotfit-agentic smoke                run the written smoke set (design §11.2)

        FLAGS
          --env <path>        .env to use (default: walk up from the cwd)
          --index <name>      override the index (default: kb-main-v2)
          --aliases <path>    override the alias table artifact
          --top <n>           sources per search, 1-20 (default: 6)
          --trace             show every tool call and what it returned
          --log               print the turn-log line (§10) to stderr after each turn
          --no-sources        do not print the source list after the answer

        SMOKE FLAGS
          --set <path>        the smoke set (default: runtime/smoke/conversations.jsonl)
          --out <dir>         where the transcript goes (default: processed/agentic/smoke)
          --tier <name>       only one tier: chat, product, currency, multiturn,
                              safety, claims, scope, adversarial, retrieval

        CHAT
          Type a question. `reset` clears the history, `exit` quits.

        NOTES
          Nothing is gated on this branch: the answer streams as it is generated
          and is never withheld (decision D3). `search` needs no chat deployment.
        """;

    public static CliCommand Parse(string[] args)
    {
        string verb = args[0].ToLowerInvariant();
        if (verb is not (Ask or Chat or Search or Prompt or Config or Smoke))
            throw new CliUsageException($"unknown verb '{args[0]}' — run with --help");

        var text = new List<string>();
        var flags = new CliFlags();

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            string Next(string name) => i + 1 < args.Length
                ? args[++i]
                : throw new CliUsageException($"{name} needs a value");

            switch (arg)
            {
                case "--env": flags = flags with { EnvPath = Next(arg) }; break;
                case "--index": flags = flags with { IndexName = Next(arg) }; break;
                case "--aliases": flags = flags with { AliasesPath = Next(arg) }; break;
                case "--trace": flags = flags with { Trace = true }; break;
                case "--log": flags = flags with { Log = true }; break;
                case "--no-sources": flags = flags with { ShowSources = false }; break;
                case "--set": flags = flags with { SmokeSet = Next(arg) }; break;
                case "--out": flags = flags with { OutDir = Next(arg) }; break;
                case "--tier": flags = flags with { Tier = Next(arg) }; break;
                case "--top":
                    string raw = Next(arg);
                    if (!int.TryParse(raw, out int top) || top < 1 || top > 20)
                        throw new CliUsageException("--top must be an integer between 1 and 20");
                    flags = flags with { Top = top };
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new CliUsageException($"unknown flag '{arg}' — run with --help");
                    text.Add(arg);
                    break;
            }
        }

        string joined = string.Join(" ", text).Trim();
        if (verb is Ask or Search && joined.Length == 0)
            throw new CliUsageException($"{verb} needs a question — dotfit-agentic {verb} \"...\"");

        return new CliCommand(verb, joined, flags);
    }
}
