using System.Text.Json;
using System.Text.Json.Serialization;
using DotFit.Agentic.Turn;

namespace DotFit.Agentic.Service;

/// <summary>Where the §10 turn log goes. One line, one turn.</summary>
public interface ITurnSink
{
    void Write(TurnLog log);
}

public sealed class JsonLinesTurnSink(TextWriter writer) : ITurnSink
{
    private readonly Lock _gate = new();

    public void Write(TurnLog log)
    {
        // Concurrent requests share stdout; a half-interleaved JSON line is
        // not a log entry.
        lock (_gate)
            writer.WriteLine(log.ToJsonLine());
    }
}

/// <summary>
/// The debug transcript: the words themselves (design §10, carried over from
/// v1's owner ruling). Off by default and off before public traffic.
///
/// It exists because nothing is gated on this branch. When an owner session
/// turns up an answer that was wrong, the turn log says the model ran three
/// searches and cited two sources — it cannot say what it claimed. This can.
/// </summary>
public interface ITranscriptSink
{
    void Write(string requestId, string question, TurnResult result, IReadOnlyList<string> sourceIds);
}

/// <summary>
/// The off case is a real sink rather than a missing registration, so "off"
/// means the text is never serialized at all rather than serialized and
/// dropped.
/// </summary>
public sealed class NullTranscriptSink : ITranscriptSink
{
    public static readonly NullTranscriptSink Instance = new();

    public void Write(string requestId, string question, TurnResult result, IReadOnlyList<string> sourceIds) { }
}

public sealed class JsonLinesTranscriptSink(TextWriter writer) : ITranscriptSink
{
    public const string SchemaName = "dotfit.agentic.transcript";
    public const string SchemaVersion = "1.0.0";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public void Write(string requestId, string question, TurnResult result, IReadOnlyList<string> sourceIds)
    {
        string line = JsonSerializer.Serialize(new
        {
            schema = SchemaName,
            schema_version = SchemaVersion,
            ts = DateTimeOffset.UtcNow,
            request_id = requestId,
            question,
            answer = result.AnswerText,
            // The tool calls with their arguments — the pair "what did it look
            // up" and "what did it then say" is the whole diagnostic.
            tool_calls = result.ToolCalls.Select(c => new
            {
                tool = c.Tool,
                argument = c.Argument,
                results = c.ResultCount,
                elapsed_ms = c.ElapsedMs,
                refusal = c.Refusal,
            }).ToArray(),
            source_ids = sourceIds,
            cited = result.CitedSources,
        }, Json);

        lock (_gate)
            writer.WriteLine(line);
    }
}
