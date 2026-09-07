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

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Fail at startup, not on the first customer question: a missing deployment
// or key is a deployment error, and a service that starts and then 500s on
// every request is harder to diagnose than one that never starts.
RuntimeOptions options = RuntimeOptions.Load(
    builder.Configuration["DotFit:EnvPath"], needs: RuntimeNeeds.Full);
if (builder.Configuration["DotFit:Index"] is { Length: > 0 } index)
    options = options with { IndexName = index };

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IKnowledgeAssistant>(
    _ => RuntimeFactory.CreateAssistant(options));

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

app.MapGet("/healthz", (RuntimeOptions opts) => Results.Ok(new
{
    status = "ok",
    index = opts.IndexName,
    // Deployment *names* are configuration, not secrets; keys are never read
    // here at all (RuntimeOptions masks them in its own repr).
    chat_deployment = opts.ChatDeployment,
    small_chat_deployment = opts.SmallChatDeployment,
    embedding_deployment = opts.EmbeddingDeployment,
}));

app.MapPost("/ask", async (
    AskRequest request,
    IKnowledgeAssistant assistant,
    HttpContext http,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question is required" });

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    // Proxies that buffer defeat the point of streaming stage events.
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var writer = new HttpSseWriter(http.Response);
    await AskStream.RunAsync(assistant, request, writer, ct);
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
