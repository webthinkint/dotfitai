using System.Text.RegularExpressions;

namespace DotFit.Agents.Config;

/// <summary>
/// Parse the repo-root <c>.env</c> secrets contract — a line-for-line mirror of
/// <c>pipeline/src/qa_pipeline/azure_config.py</c> (the two must stay in
/// lockstep: same strictness, same comment/quote semantics, same
/// "errors name variables, never values" rule). Key material is never echoed.
/// </summary>
public static partial class EnvFile
{
    public const string FileName = ".env";
    public const string ExampleFileName = ".env.example";

    /// <summary>.env missing, malformed, or incomplete. Carries variable names only.</summary>
    public sealed class EnvFileException(string message) : Exception(message);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VarNameRegex();

    /// <summary>Parse KEY=VALUE lines. Strict: bad lines and duplicates raise.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>();
        int lineno = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineno++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export "))
                line = line["export ".Length..].TrimStart();

            int eq = line.IndexOf('=');
            var key = eq <= 0 ? "" : line[..eq].Trim();
            if (eq <= 0 || !VarNameRegex().IsMatch(key))
                throw new EnvFileException($"{FileName} line {lineno}: expected KEY=VALUE");
            if (values.ContainsKey(key))
                throw new EnvFileException($"{FileName} line {lineno}: duplicate key {key}");

            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith('\'') || value.StartsWith('"'))
            {
                // quoted value: use it as-is when the rest is empty or a comment
                char q = value[0];
                int end = value.IndexOf(q, 1);
                if (end != -1)
                {
                    var rest = value[(end + 1)..].Trim();
                    if (rest.Length == 0 || rest.StartsWith('#'))
                        value = value[1..end];
                }
            }
            else if (value.StartsWith('#'))
            {
                value = "";
            }
            else
            {
                int hash = IndexOfWhitespaceHash(value);
                if (hash != -1)
                    value = value[..hash].TrimEnd();
            }
            values[key] = value;
        }
        return values;
    }

    /// <summary>First index where a whitespace run begins that ends in '#'.</summary>
    private static int IndexOfWhitespaceHash(string value)
    {
        for (int i = 0; i < value.Length - 1; i++)
            if ((value[i] == ' ' || value[i] == '\t') && value[i + 1] == '#')
                return i;
        return -1;
    }

    /// <summary>Read and parse <paramref name="path"/> (UTF-8; a Windows BOM is tolerated).</summary>
    public static IReadOnlyDictionary<string, string> ReadFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path); // StreamReader strips the BOM, like utf-8-sig
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EnvFileException($"could not read {path}: {e.Message}");
        }
        return Parse(text);
    }
}
