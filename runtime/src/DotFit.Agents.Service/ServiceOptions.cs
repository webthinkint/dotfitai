using System.Security.Cryptography;
using System.Text;
using DotFit.Agents.Answering;
using DotFit.Agents.Config;

namespace DotFit.Agents.Service;

/// <summary>
/// The hardening contract of the SSE service (plan §11, progress open item 22):
/// the four things the transport owes a deployment that the pipeline itself has
/// no opinion about — who may call it, how long a question may be, how long a
/// request may run, and where a refused customer is sent.
///
/// Read from the same root <c>.env</c> the rest of the runtime uses
/// (<see cref="RuntimeOptions"/>), because these are deployment values and one
/// of them is a secret; the three <c>DOTFIT_SERVICE_*</c> variables are
/// service-only and the CLI never looks at them.
///
/// **Auth is fail-closed.** The service has no authentication of its own beyond
/// this shared secret and must not be reachable from the public internet, so a
/// missing <c>DOTFIT_SERVICE_API_KEY</c> stops the boot rather than quietly
/// serving an open endpoint — the same posture as a missing deployment name
/// (see <c>Program</c>). Running without auth is possible but must be *said*:
/// <c>DOTFIT_SERVICE_AUTH=none</c>, for a local smoke or a deployment that
/// terminates mTLS in front. <c>/healthz</c> is never authenticated: it is the
/// liveness probe and returns only configuration, never key material.
///
/// CORS and rate limiting are deliberately absent (item 22): there is exactly
/// one trusted server-side caller and no browser origin, and the website server
/// relays the stream. Adding either would be guessing at a deployment we do not
/// have.
/// </summary>
public sealed record ServiceOptions
{
    public const string ApiKeyVar = "DOTFIT_SERVICE_API_KEY";
    public const string AuthVar = "DOTFIT_SERVICE_AUTH";
    public const string MaxQuestionCharsVar = "DOTFIT_SERVICE_MAX_QUESTION_CHARS";
    public const string TimeoutSecondsVar = "DOTFIT_SERVICE_TIMEOUT_SECONDS";

    /// <summary>
    /// Long enough for a pasted customer email — the QA corpus is full of them
    /// — and short enough that the guardrail and rewrite prompts cannot be used
    /// as a free completion endpoint. A question over the limit is a 400, not a
    /// silent truncation: truncating changes the question, and the answer would
    /// be to something the customer did not ask.
    /// </summary>
    public const int DefaultMaxQuestionChars = 2_000;

    /// <summary>
    /// The whole §11 chain is five model calls, so this is generous on purpose;
    /// it exists to stop a wedged upstream call from holding a connection (and
    /// its Azure quota) open forever, not to bound a normal answer, which takes
    /// seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// Bounds <c>top</c> on the wire. The default is 8 and the caller is told to
    /// leave it unset; the cap is here because <c>top</c> multiplies the answer
    /// prompt's size, so an unbounded one is a cost amplifier handed to the
    /// caller.
    /// </summary>
    public const int MaxTop = 20;

    /// <summary>256 KB. A question plus 8 trimmed history turns is kilobytes.</summary>
    public const long MaxRequestBytes = 256 * 1024;

    /// <summary>The shared secret, or <c>null</c> when auth is explicitly off.</summary>
    public string? ApiKey { get; init; }
    public int MaxQuestionChars { get; init; } = DefaultMaxQuestionChars;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    /// <summary>Where the two handoff templates send a refused customer (item 22).</summary>
    public string? SupportContact { get; init; } = Prompts.DefaultSupportContact;

    /// <summary>True when <see cref="AuthVar"/> said <c>none</c>.</summary>
    public bool AuthDisabled => ApiKey is null;

    /// <summary>
    /// Load the service slice from the <c>.env</c> the given runtime options
    /// came from. <paramref name="options"/> supplies the support route, which
    /// is library-wide rather than service-only.
    ///
    /// The **process environment wins** over the file for these three
    /// variables, which is the one place the service departs from the rest of
    /// the <c>.env</c> contract and is deliberate: the shared secret is the only
    /// value a deployment may want to inject as a container/App Service setting
    /// rather than write into a file that ends up in an image layer. The Azure
    /// keys keep the file-only rule, because <c>azure_config.py</c> mirrors it
    /// and these three variables are not in that mirror.
    /// </summary>
    public static ServiceOptions Load(RuntimeOptions options)
    {
        var values = new Dictionary<string, string>(EnvFile.ReadFile(options.EnvFilePath));
        foreach (string name in new[] { ApiKeyVar, AuthVar, MaxQuestionCharsVar, TimeoutSecondsVar })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v)
                values[name] = v;
        return FromValues(values, options);
    }

    /// <summary>Validate parsed values. Errors name variables, never values.</summary>
    internal static ServiceOptions FromValues(
        IReadOnlyDictionary<string, string> values, RuntimeOptions options)
    {
        string? Value(string name)
        {
            string v = values.TryGetValue(name, out string? s) ? s.Trim() : "";
            return v.Length == 0 ? null : v;
        }

        string? auth = Value(AuthVar);
        string? apiKey = Value(ApiKeyVar);
        bool off = string.Equals(auth, "none", StringComparison.OrdinalIgnoreCase);
        if (auth is not null && !off)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {AuthVar} must be \"none\" or be left unset — " +
                $"set {ApiKeyVar} to require a shared secret");
        if (!off && apiKey is null)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: required variable {ApiKeyVar} is missing or empty — the " +
                "service has no authentication of its own and must not be reachable without it; " +
                $"set {AuthVar}=none only when something in front of it authenticates the caller");
        if (off && apiKey is not null)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {AuthVar}=none and {ApiKeyVar} are both set — one of them " +
                "is a mistake, and guessing which would either expose the service or reject the caller");
        // A short secret is a guessable one, and this is the only thing between
        // the caller and the endpoint.
        if (apiKey is { Length: < 16 })
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {ApiKeyVar} must be at least 16 characters");

        return new ServiceOptions
        {
            ApiKey = apiKey,
            MaxQuestionChars = Positive(MaxQuestionCharsVar, Value(MaxQuestionCharsVar), DefaultMaxQuestionChars),
            RequestTimeout = TimeSpan.FromSeconds(
                Positive(TimeoutSecondsVar, Value(TimeoutSecondsVar), DefaultTimeoutSeconds)),
            SupportContact = options.SupportContact,
        };
    }

    private static int Positive(string name, string? raw, int fallback)
    {
        if (raw is null)
            return fallback;
        if (!int.TryParse(raw, out int v) || v <= 0)
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {name} must be a positive whole number");
        return v;
    }

    /// <summary>
    /// Whether an <c>Authorization</c> header carries the shared secret.
    /// Compared in fixed time: a length- or prefix-dependent comparison on the
    /// one secret guarding the endpoint is a guessing oracle.
    /// </summary>
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (ApiKey is null)
            return true;                            // AUTH=none, said out loud
        const string scheme = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        string presented = authorizationHeader[scheme.Length..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(ApiKey));
    }

    /// <summary>
    /// Why a request cannot be served, or <c>null</c> when it can — the checks
    /// that must answer with a status code, so they run before the SSE stream
    /// opens and commits the response to 200.
    /// </summary>
    public string? Reject(AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return "question is required";
        if (request.Question.Length > MaxQuestionChars)
            return $"question is longer than the {MaxQuestionChars} character limit";
        if (request.Top is { } top && (top < 1 || top > MaxTop))
            return $"top must be between 1 and {MaxTop}";
        return request.TryReadHistory(out _, out string? historyError) ? null : historyError;
    }

    /// <summary>Masked representation — the shared secret is never rendered.</summary>
    public override string ToString() =>
        $"ServiceOptions(auth={(ApiKey is null ? "none" : "shared-secret***")}, " +
        $"max_question_chars={MaxQuestionChars}, timeout={RequestTimeout.TotalSeconds:0}s, " +
        $"support='{SupportContact ?? "unset"}')";
}
