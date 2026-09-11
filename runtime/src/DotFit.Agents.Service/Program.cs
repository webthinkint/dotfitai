using System.Text;
using System.Text.Json;
using DotFit.Agents;
using DotFit.Agents.Aliases;
using DotFit.Agents.Config;
using DotFit.Agents.Service;

// dotfit-agent-service — the §11 runtime behind an SSE endpoint (plan §11,
// "SSE stream to widget"). The pipeline itself is DotFit.Agents; this project
// is transport: config load, one endpoint, one health check.
//
// Delivery is Gated and the client cannot ask for anything else — see
// AskStream for why (§11 "streaming vs. gating").
//
// Hardening is ServiceOptions (open item 22): a shared secret on /ask, a
// question-length cap, a request timeout, and the support route the handoff
// templates end on. Deliberately *not* here: CORS and rate limiting — one
// trusted server-side caller, no browser origin.
//
// Every request also writes one VerdictLog line to stdout (open item 20) —
// what was decided, never what was said.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Fail at startup, not on the first customer question: a missing deployment
// or key is a deployment error, and a service that starts and then 500s on
// every request is harder to diagnose than one that never starts.
RuntimeOptions options = RuntimeOptions.Load(
    builder.Configuration["DotFit:EnvPath"], needs: RuntimeNeeds.Full);
if (builder.Configuration["DotFit:Index"] is { Length: > 0 } index)
    options = options with { IndexName = index };

// The hardening slice, same file and same moment (open item 22): a missing
// shared secret is a boot failure, because an endpoint with no auth that
// answers anyway is the one mistake this service cannot survive.
ServiceOptions service = ServiceOptions.Load(options);

// One request body cannot be larger than a question plus eight trimmed history
// turns. Kestrel's 30 MB default is for file uploads, and this endpoint feeds
// model prompts.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ServiceOptions.MaxRequestBytes);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(service);
builder.Services.AddSingleton<IKnowledgeAssistant>(
    _ => RuntimeFactory.CreateAssistant(options));

// One verdict line per request, to stdout (open item 20). Not optional and not
// configurable: with the preview free to ship at any state (item 21 ruling),
// this record is the only reconstruction of what an audience was shown, and a
// switch to turn it off is a switch to lose that. It holds no question and no
// answer text, so there is nothing to turn off for privacy either — see
// VerdictLog.
builder.Services.AddSingleton<IVerdictSink>(_ => new JsonLinesVerdictSink(Console.Out));

// Request binding must use the same naming policy the responses do. It does
// not by default, and the failure is silent: `conversation_id` bound to
// nothing, so every turn looked like a new conversation and re-sent the
// disclosure. One policy, both directions.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = AskStream.Json.PropertyNamingPolicy;
    json.SerializerOptions.DefaultIgnoreCondition = AskStream.Json.DefaultIgnoreCondition;
});

WebApplication app = builder.Build();

app.MapGet("/healthz", (RuntimeOptions opts, ServiceOptions svc) => Results.Ok(new
{
    status = "ok",
    index = opts.IndexName,
    // Deployment *names* are configuration, not secrets; keys are never read
    // here at all (RuntimeOptions masks them in its own repr).
    chat_deployment = opts.ChatDeployment,
    small_chat_deployment = opts.SmallChatDeployment,
    embedding_deployment = opts.EmbeddingDeployment,
    // The hardening posture, readable without a key on purpose: "auth": "none"
    // on a deployment that was meant to require a secret is the single thing an
    // operator most needs to be able to see, and the limits are the caller's to
    // know rather than to discover by being rejected. The secret itself is
    // never rendered (ServiceOptions.ToString masks it too).
    auth = svc.AuthDisabled ? "none" : "shared-secret",
    max_question_chars = svc.MaxQuestionChars,
    timeout_seconds = (int)svc.RequestTimeout.TotalSeconds,
}));

app.MapPost("/ask", async (
    AskRequest request,
    IKnowledgeAssistant assistant,
    ServiceOptions service,
    IVerdictSink verdicts,
    HttpContext http,
    CancellationToken ct) =>
{
    // Auth first, and before anything is parsed far enough to cost a model
    // call. 401 carries no detail — which header was wrong is information only
    // a guesser wants. /healthz stays open: it is the liveness probe and
    // returns configuration, never key material.
    if (!service.IsAuthorized(http.Request.Headers.Authorization))
        return Results.Unauthorized();

    // Validate while a status code still means something: once the SSE stream
    // opens the response is already 200, so a malformed history or an
    // over-long question has no way left to be reported as an error.
    if (service.Reject(request) is { } error)
        return Results.BadRequest(new { error });

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    // Proxies that buffer defeat the point of streaming stage events.
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var writer = new HttpSseWriter(http.Response);
    await AskStream.RunAsync(assistant, request, writer, ct, service, verdicts);
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

/// <summary>Exposed so the test host can boot this app.</summary>
public partial class Program;
