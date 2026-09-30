using System.Security.Cryptography;
using System.Text;
using DotFit.Assistant.Config;

namespace DotFit.Assistant.Service;

/// <summary>
/// Transport hardening for <c>POST /ask</c>. A shared secret that is missing
/// **fails the boot** rather than serving an open endpoint;
/// <c>DOTFIT_SERVICE_AUTH=none</c> is the explicit opt-out for a deployment
/// behind mTLS.
///
/// Deliberately absent: CORS and rate limiting. One trusted server-side
/// caller, no browser origin.
/// </summary>
public sealed record ServiceOptions
{
    public const string ApiKeyVar = "DOTFIT_SERVICE_API_KEY";
    public const string AuthVar = "DOTFIT_SERVICE_AUTH";
    public const string MaxQuestionCharsVar = "DOTFIT_SERVICE_MAX_QUESTION_CHARS";
    public const string TimeoutSecondsVar = "DOTFIT_SERVICE_TIMEOUT_SECONDS";

    /// <summary>
    /// Full question and answer text to stdout. Off by default, on in the
    /// preview unit, **off before public traffic**. With nothing gated it is
    /// the only way to see what a bad turn actually said.
    /// </summary>
    public const string DebugTranscriptVar = "DOTFIT_ASSISTANT_DEBUG_TRANSCRIPT";

    public const int DefaultMaxQuestionChars = 2_000;
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTop = AssistantOptions.MaxTopCeiling;
    public const long MaxRequestBytes = 256 * 1024;
    public const int MaxIdChars = 64;

    public string? ApiKey { get; init; }
    public int MaxQuestionChars { get; init; } = DefaultMaxQuestionChars;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    public string? SupportContact { get; init; } = Prompting.SystemPrompt.DefaultSupportContact;
    public bool DebugTranscript { get; init; }

    public bool AuthDisabled => ApiKey is null;

    /// <summary>
    /// Load the service slice from the <c>.env</c> the given runtime options
    /// came from. <paramref name="options"/> supplies the support route, which
    /// is library-wide rather than service-only.
    ///
    /// The **process environment wins** over the file for this class's five
    /// variables — the one departure from the <c>.env</c> contract, and a
    /// deliberate one: a deployment injects its posture as a unit /
    /// container setting rather than editing a shared file, which is exactly
    /// how the preview unit turns the debug transcript on. The Azure keys keep
    /// the file-only rule, because <c>azure_config.py</c> mirrors it and these
    /// variables are not in that mirror.
    /// </summary>
    public static ServiceOptions Load(RuntimeOptions options)
    {
        var values = new Dictionary<string, string>(EnvFile.ReadFile(options.EnvFilePath));
        foreach (string name in new[]
                 { ApiKeyVar, AuthVar, MaxQuestionCharsVar, TimeoutSecondsVar, DebugTranscriptVar })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v)
                values[name] = v;
        return FromValues(values, options.SupportContact);
    }

    internal static ServiceOptions FromValues(
        IReadOnlyDictionary<string, string> values, string? supportContact)
    {
        string? Value(string name)
        {
            string v = values.TryGetValue(name, out string? s) ? s.Trim() : "";
            return v.Length == 0 ? null : v;
        }

        string? auth = Value(AuthVar);
        string? apiKey = Value(ApiKeyVar);
        bool authOff = string.Equals(auth, "none", StringComparison.OrdinalIgnoreCase);

        if (!authOff && apiKey is null)
        {
            // The one failure this service must not survive: booting with no
            // auth and answering anyway. Saying "none" out loud is allowed;
            // forgetting is not.
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {ApiKeyVar} is missing — POST /ask would be an open endpoint. " +
                $"Set a shared secret, or set {AuthVar}=none if this deployment is behind mTLS.");
        }
        if (authOff && apiKey is not null)
        {
            throw new EnvFile.EnvFileException(
                $"{EnvFile.FileName}: {AuthVar}=none and {ApiKeyVar} is set — say one or the other, " +
                "not both, so the posture is never a guess.");
        }

        int Int(string name, int fallback, int min, int max)
        {
            if (Value(name) is not { } raw)
                return fallback;
            if (!int.TryParse(raw, out int parsed) || parsed < min || parsed > max)
                throw new EnvFile.EnvFileException(
                    $"{EnvFile.FileName}: {name} must be an integer between {min} and {max}");
            return parsed;
        }

        return new ServiceOptions
        {
            ApiKey = authOff ? null : apiKey,
            MaxQuestionChars = Int(MaxQuestionCharsVar, DefaultMaxQuestionChars, 1, 20_000),
            RequestTimeout = TimeSpan.FromSeconds(Int(TimeoutSecondsVar, DefaultTimeoutSeconds, 5, 600)),
            SupportContact = supportContact,
            DebugTranscript = Value(DebugTranscriptVar) is "1" or "true" or "on",
        };
    }

    /// <summary>
    /// The request ceiling must sit *above* the loop's own hard ceiling:
    /// the loop is meant to lose to itself, ending in a handoff the customer
    /// reads, rather than losing to the host mid-sentence. Both are independent
    /// environment knobs, so the ordering is checked at boot — this service
    /// already prefers failing at startup over failing on a customer's
    /// question.
    /// </summary>
    public void RequireRoomForTurn(AssistantOptions assistantOptions)
    {
        if (RequestTimeout > assistantOptions.HardTimeout)
            return;
        throw new EnvFile.EnvFileException(
            $"{EnvFile.FileName}: {TimeoutSecondsVar}={RequestTimeout.TotalSeconds:0} is not above the " +
            $"assistant's {assistantOptions.HardTimeout.TotalSeconds:0}-second turn ceiling (raised by " +
            $"{AssistantOptions.TurnTimeoutVar}). The request timeout must be the outer one, or a slow turn " +
            "ends with no answer instead of with a handoff.");
    }

    /// <summary>
    /// Fixed-time comparison, so a wrong secret takes the same time to reject
    /// however much of it was right.
    /// </summary>
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (AuthDisabled)
            return true;
        const string scheme = "Bearer ";
        string header = authorizationHeader?.Trim() ?? "";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header[scheme.Length..].Trim()),
            Encoding.UTF8.GetBytes(ApiKey!));
    }

    /// <summary>
    /// Everything that must be rejected with a status code, checked before the
    /// stream opens. The first SSE frame commits the response to 200 and there
    /// is no status code left to reject with after it.
    /// </summary>
    public string? Reject(AskBody? request)
    {
        if (request is null)
            return "body must be a JSON object";
        if (string.IsNullOrWhiteSpace(request.Question))
            return "question must not be empty";
        if (request.Question.Length > MaxQuestionChars)
            return $"question must be {MaxQuestionChars} characters or fewer";
        if (request.Top is int top && (top < 1 || top > MaxTop))
            return $"top must be between 1 and {MaxTop}";
        if (request.ConversationId is { Length: > MaxIdChars })
            return $"conversation_id must be {MaxIdChars} characters or fewer";
        if (request.RequestId is { Length: > MaxIdChars })
            return $"request_id must be {MaxIdChars} characters or fewer";
        foreach (AskBody.Turn turn in request.History ?? [])
        {
            if (!ConversationHistory.TryParseRole(turn.Role, out _))
                return $"history role must be 'user' or 'assistant' (got '{turn.Role}')";
        }
        return null;
    }

    /// <summary>Masked — the shared secret is never rendered.</summary>
    public override string ToString() =>
        $"ServiceOptions(auth={(AuthDisabled ? "none" : "shared-secret ***")}, " +
        $"max_question_chars={MaxQuestionChars}, timeout={RequestTimeout.TotalSeconds:0}s, " +
        $"debug_transcript={(DebugTranscript ? "on" : "off")})";
}
