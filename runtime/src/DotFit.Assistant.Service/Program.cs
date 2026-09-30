using DotFit.Assistant.Cost;
using System.Text;
using System.Text.Json;
using DotFit.Assistant;
using DotFit.Assistant.Config;
using DotFit.Assistant.Service;
using DotFit.Assistant.Aliases;
using DotFit.Assistant.Sources;
using DotFit.Assistant.Prompting;

// dotfit-service — the assistant loop behind an SSE endpoint.
// Transport only: config load, one endpoint, one health check. Deltas stream
// live and nothing is withheld; see AskStream for the event contract.
//
// Every request writes one turn-log line to stdout — what the model did,
// never what it or the customer said. DOTFIT_ASSISTANT_DEBUG_TRANSCRIPT adds the
// words, for preview debugging only.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Fail at startup, not on the first customer question. If it is up, it is
// configured — including its auth posture.
RuntimeOptions options = RuntimeOptions.Load(
    builder.Configuration["DotFit:EnvPath"], needs: AssistantFactory.Needs);
if (builder.Configuration["DotFit:Index"] is { Length: > 0 } index)
    options = options with { IndexName = index };

ServiceOptions service = ServiceOptions.Load(options);
AssistantOptions assistantOptions = AssistantOptions.Load(options.EnvFilePath);
// The sheet the per-turn `cost` block is priced against. Validated here
// for the same reason everything else is: if the service is up, the numbers
// it emits are interpretable — which sheet, which currency.
PriceSheet prices = PriceSheet.Load(options.EnvFilePath);
// Both ceilings are independent knobs; inverted, the host kills the turn before
// the loop can hand off, and the customer gets a truncated stream.
service.RequireRoomForTurn(assistantOptions);
AliasTable aliases = AliasTable.Load(options.AliasTablePath);
// Assembled once, here, so a broken prompt variant stops the boot.
SourceRegistry registry = SourceRegistry.Load(options.SourcesPath);
AssembledPrompt prompt = AssembledPrompt.Load(options, assistantOptions, aliases, registry);

// A question plus eight trimmed history turns. Kestrel's 30 MB default is for
// file uploads; this endpoint feeds model prompts.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ServiceOptions.MaxRequestBytes);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(service);
builder.Services.AddSingleton(assistantOptions);
builder.Services.AddSingleton(prices);
builder.Services.AddSingleton(prompt);
builder.Services.AddSingleton<IDotFitAssistant>(
    _ => AssistantFactory.Create(options, assistantOptions, aliases, prices: prices, prompt: prompt, registry: registry));

// Not optional and not configurable. With nothing gated, this log is the only
// reconstruction of what an audience was shown — and it holds no
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

app.MapGet("/healthz", (RuntimeOptions opts, ServiceOptions svc, AssistantOptions agent, PriceSheet sheet, AssembledPrompt prompt) => Results.Ok(new
{
    status = "ok",
    runtime = "dotfit-assistant",
    index = opts.IndexName,
    prompt_variant = prompt.Variant,
    prompt_version = prompt.Version,
    // Deployment names are configuration, not secrets; no key is read here.
    chat_deployment = opts.ChatDeployment,
    embedding_deployment = opts.EmbeddingDeployment,
    // Readable without a key on purpose: "auth": "none" on a deployment that
    // was meant to require a secret is the thing an operator most needs to see.
    auth = svc.AuthDisabled ? "none" : "shared-secret",
    max_question_chars = svc.MaxQuestionChars,
    timeout_seconds = (int)svc.RequestTimeout.TotalSeconds,
    debug_transcript = svc.DebugTranscript ? "on" : "off",
    // The loop's budgets, so a latency complaint can be read against them.
    max_tool_calls = agent.MaxToolCalls,
    turn_timeout_seconds = (int)agent.TurnTimeout.TotalSeconds,
    default_top = agent.DefaultTop,
    // The prices behind `result.cost`, said out loud so an operator can check
    // what a turn is being priced against without asking anyone. The search
    // rate is a placeholder until real billing data replaces it — the sheet
    // id is the version to bump when it does.
    price_sheet = sheet.Id,
    price_currency = sheet.Currency,
    price_chat_usd_per_1m = new
    {
        input = sheet.ChatInputPerMillion,
        cached_input = sheet.ChatCachedInputPerMillion,
        output = sheet.ChatOutputPerMillion,
    },
    price_embedding_usd_per_1m = sheet.EmbeddingPerMillion,
    price_search_usd_per_1k = sheet.SearchPerThousand,
    // Said out loud: there is no retraction event and no withheld answer.
    gating = "none",
}));

app.MapPost("/ask", async (
    AskBody request,
    IDotFitAssistant assistant,
    ServiceOptions service,
    ITurnSink turns,
    ITranscriptSink transcripts,
    AssembledPrompt prompt,
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
    // which undoes live streaming in transit.
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var writer = new HttpSseWriter(http.Response);
    await AskStream.RunAsync(assistant, request, writer, service, turns, transcripts, ct, prompt);
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
