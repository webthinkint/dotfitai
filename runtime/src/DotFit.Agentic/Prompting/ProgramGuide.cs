using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DotFit.Agentic.Prompting;

/// <summary>
/// dotFIT's supplement program guide (design §7.4): how dotFIT's founder
/// builds a program — screening, baseline, goal branches, add-ons, overlap
/// check — as a pick list of current dotFIT products with doses.
///
/// Embedded at build time from
/// <c>processed/podcasts/neal-spruce-dotfit-decision-tree.md</c>, so the
/// deployed service carries the revision it was built with and there is no
/// file to go missing at boot. Owner-ruled 2026-09-17: used as written, pending
/// a dotFIT review of its doses (open item 12).
///
/// It is the assistant's working method for program questions, **not a
/// numbered source**: <c>get_program_guide</c> returns it without touching the
/// ledger, and the answer does not cite it (owner-ruled 2026-09-17). Because it
/// is uncited, <see cref="Version"/> is what ties an answer to the revision
/// that shaped it — the turn log carries it whenever the guide was read, the
/// same way a cost figure carries its price sheet.
///
/// Not carried in the system prompt: at ~4,500 tokens it would triple the
/// prompt on every turn, greetings included (open item 10). Only a turn that
/// asks for a program pays for it.
/// </summary>
public static class ProgramGuide
{
    private const string ResourceName = "DotFit.Agentic.Prompting.program-guide.md";

    private static readonly Lazy<string> LazyText = new(Load);
    private static readonly Lazy<string> LazyVersion = new(() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)))[..12]);

    /// <summary>The guide, as written.</summary>
    public static string Text => LazyText.Value;

    /// <summary>First 12 hex characters of the guide's SHA-256 — changes whenever the text does.</summary>
    public static string Version => LazyVersion.Value;

    private static string Load()
    {
        using Stream stream = typeof(ProgramGuide).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing from {typeof(ProgramGuide).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
