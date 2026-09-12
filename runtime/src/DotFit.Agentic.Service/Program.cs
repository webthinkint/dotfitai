using System.Text;
using System.Text.Json;
using DotFit.Agentic;
using DotFit.Agentic.Config;
using DotFit.Agentic.Service;
using DotFit.Agents.Aliases;
using DotFit.Agents.Config;

// dotfit-agentic-service — the §6 loop behind an SSE endpoint (design §9).
// Transport only: config load, one endpoint, one health check.
//
// The caller contract is v1's, deliberately (decision D6): the website relay
// already speaks it, so a preview can point at either runtime. What differs is
// the event stream — deltas are live, `source` is new, `retraction` is gone,
// because nothing on this branch gates (D3). See AskStream.
//
// Every request writes one turn-log line to stdout (§10) — what the model did,
// never what it or the customer said. DOTFIT_AGENTIC_DEBUG_TRANSCRIPT adds the
// words, for preview debugging only.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Fail at startup, not on the first customer question. If it is up, it is
// configured — including its auth posture.
RuntimeOptions options = RuntimeOptions.Load(
    builder.Configuration["DotFit:EnvPath"], needs: AgenticFactory.Needs);
if (builder.Configuration["DotFit:Index"] is { Length: > 0 } index)
    options = options with { IndexName = index };

AgenticServiceOptions service = AgenticServiceOptions.Load(options);
AgenticOptions agentic = AgenticOptions.Load(options.EnvFilePath);
AliasTable aliases = AliasTable.Load(options.AliasTablePath);

// A question plus eight trimmed history turns. Kestrel's 30 MB default is for
// file uploads; this endpoint feeds model prompts.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = AgenticServiceOptions.MaxRequestBytes);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(service);
builder.Services.AddSingleton(agentic);
builder.Services.AddSingleton<IAgenticAssistant>(
    _ => AgenticFactory.Create(options, agentic, aliases));

// Not optional and not configurable. With nothing gated, this log is the only
// reconstruction of what an audience was shown (§8.3) — and it holds no
// question and no answer text, so there is nothing to switch off for privacy.
builder.Services.AddSingleton<ITurnSink>(_ => new JsonLinesTurnSink(Console.Out));

if (service.DebugTranscript)
{
    builder.Services.AddSingleton<ITranscriptSink>(_ => new JsonLinesTranscriptSink(Console.Out));
    Console.WriteLine(
        $"{JsonLinesTranscriptSink.SchemaName}: DEBUG TRANSCRIPT ENABLED — full question and answer " +
        "text will be logged to stdout (preview-only; off before public customer traffic)");
}
else
{
    builder.Services.AddSingleton<ITranscriptSink>(_ => NullTranscriptSink.Instance);
}

// Request binding must use the same naming policy the responses do. It does
// not by default, and the failure is silent: `conversation_id` binds to
// nothing, so every turn looks like a new conversation and re-sends the
// disclosure.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = AskStream.Json.PropertyNamingPolicy;
    json.SerializerOptions.DefaultIgnoreCondition = AskStream.Json.DefaultIgnoreCondition;
});

WebApplication app = builder.Build();

app.MapGet("/healthz", (RuntimeOptions opts, AgenticServiceOptions svc, AgenticOptions agent) => Results.Ok(new
{
    status = "ok",
    runtime = "agentic",
    index = opts.IndexName,
    // Deployment names are configuration, not secrets; no key is read here.
    // The small-chat deployment is absent on purpose — nothing calls one (§10).
    chat_deployment = opts.ChatDeployment,
    embedding_deployment = opts.EmbeddingDeployment,
    // Readable without a key on purpose: "auth": "none" on a deployment that
    // was meant to require a secret is the thing an operator most needs to see.
    auth = svc.AuthDisabled ? "none" : "shared-secret",
    max_question_chars = svc.MaxQuestionChars,
    timeout_seconds = (int)svc.RequestTimeout.TotalSeconds,
    debug_transcript = svc.DebugTranscript ? "on" : "off",
    // The §6 budgets, so a latency complaint can be read against them.
    max_tool_calls = agent.MaxToolCalls,
    turn_timeout_seconds = (int)agent.TurnTimeout.TotalSeconds,
    default_top = agent.DefaultTop,
    // Said out loud, because it is the difference from v1 a caller most needs
    // to know: there is no retraction event and no withheld answer.
    gating = "none",
}));

app.MapPost("/ask", async (
    AskBody request,
    IAgenticAssistant assistant,
    AgenticServiceOptions service,
    ITurnSink turns,
    ITranscriptSink transcripts,
    HttpContext http,
    CancellationToken ct) =>
{
    // Auth first, before anything costs a model call. The 401 carries no
    // detail — which header was wrong is information only a guesser wants.
    if (!service.IsAuthorized(http.Request.Headers.Authorization))
        return Results.Unauthorized();

    // Validate while a status code still means something: the first SSE frame
    // commits the response to 200.
    if (service.Reject(request) is { } error)
        return Results.BadRequest(new { error });

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    // A proxy that buffers turns live streaming back into one lump at the end,
    // which is the entire point of this branch undone in transit.
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var writer = new HttpSseWriter(http.Response);
    await AskStream.RunAsync(assistant, request, writer, service, turns, transcripts, ct);
    return Results.Empty;
});

app.Run();

/// <summary>Writes SSE frames straight to the response body.</summary>
internal sealed class HttpSseWriter(HttpResponse response) : ISseWriter
{
    public async Task WriteAsync(string eventName, object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, AskStream.Json);
        // SSE frames are newline-delimited, so a payload containing a newline
        // would end the frame early. JSON escapes them, which is why the data
        // line is always JSON and never raw text.
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", Encoding.UTF8, ct);
    }

    public Task FlushAsync(CancellationToken ct) => response.Body.FlushAsync(ct);
}

/// <summary>Exposed so a test host can boot this app.</summary>
public partial class Program;
