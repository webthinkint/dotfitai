namespace DotFit.Agents.Tests;

/// <summary>Temp directory for file-backed tests (best-effort cleanup).</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string tag = "t")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"dotfit-agents-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
