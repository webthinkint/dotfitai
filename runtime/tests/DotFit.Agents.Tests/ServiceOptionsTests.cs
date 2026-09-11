using DotFit.Agents.Answering;
using DotFit.Agents.Config;
using DotFit.Agents.Service;

namespace DotFit.Agents.Tests;

/// <summary>
/// The service hardening contract (progress open item 22): who may call
/// <c>/ask</c>, how long a question may be, and where a refused customer is
/// sent. The posture under test is fail-closed — an endpoint with no auth that
/// answers anyway is the one mistake this service cannot survive — so most of
/// these assert that a *plausible misconfiguration* stops the boot.
/// </summary>
public class ServiceOptionsTests
{
    private const string Secret = "a-shared-secret-long-enough";

    private static RuntimeOptions Runtime(string? supportContact = Prompts.DefaultSupportContact) =>
        new()
        {
            AliasTablePath = "aliases.json",
            EnvFilePath = ".env",
            SupportContact = supportContact,
        };

    private static ServiceOptions From(params string[] lines) =>
        ServiceOptions.FromValues(EnvFile.Parse(string.Join("\n", lines)), Runtime());

    [Fact]
    public void SharedSecretIsRequiredUnlessAuthIsTurnedOffOutLoud()
    {
        var e = Assert.Throws<EnvFile.EnvFileException>(() => From());
        Assert.Contains(ServiceOptions.ApiKeyVar, e.Message);
        // The message has to name the escape hatch, or the next person deletes
        // the check instead of making the decision.
        Assert.Contains(ServiceOptions.AuthVar, e.Message);
        // And never the value, same rule as the rest of the .env contract.
        Assert.DoesNotContain(Secret, e.Message);
    }

    [Fact]
    public void AuthNoneIsAllowedAndSaysSo()
    {
        ServiceOptions o = From($"{ServiceOptions.AuthVar}=none");
        Assert.True(o.AuthDisabled);
        Assert.True(o.IsAuthorized(null));          // nothing to check against
    }

    [Fact]
    public void AuthNoneTogetherWithAKeyIsRejectedRatherThanGuessed()
    {
        // One of the two is a mistake, and guessing which would either expose
        // the service or reject the caller.
        var e = Assert.Throws<EnvFile.EnvFileException>(
            () => From($"{ServiceOptions.AuthVar}=none", $"{ServiceOptions.ApiKeyVar}={Secret}"));
        Assert.Contains(ServiceOptions.AuthVar, e.Message);
        Assert.Contains(ServiceOptions.ApiKeyVar, e.Message);
    }

    [Fact]
    public void AnUnknownAuthModeIsNotSilentlyTreatedAsOff()
    {
        Assert.Throws<EnvFile.EnvFileException>(
            () => From($"{ServiceOptions.AuthVar}=off"));
    }

    [Fact]
    public void AShortSecretIsRejected()
    {
        var e = Assert.Throws<EnvFile.EnvFileException>(
            () => From($"{ServiceOptions.ApiKeyVar}=hunter2"));
        Assert.Contains(ServiceOptions.ApiKeyVar, e.Message);
    }

    [Fact]
    public void OnlyTheBearerSecretAuthorizes()
    {
        ServiceOptions o = From($"{ServiceOptions.ApiKeyVar}={Secret}");

        Assert.True(o.IsAuthorized($"Bearer {Secret}"));
        Assert.True(o.IsAuthorized($"bearer {Secret}"));   // scheme is case-insensitive
        Assert.False(o.IsAuthorized(null));
        Assert.False(o.IsAuthorized(""));
        Assert.False(o.IsAuthorized(Secret));              // no scheme
        Assert.False(o.IsAuthorized($"Bearer {Secret}x"));
        Assert.False(o.IsAuthorized($"Bearer {Secret[..10]}"));   // prefix is not enough
    }

    [Fact]
    public void LimitsDefaultAndParse()
    {
        ServiceOptions d = From($"{ServiceOptions.AuthVar}=none");
        Assert.Equal(ServiceOptions.DefaultMaxQuestionChars, d.MaxQuestionChars);
        Assert.Equal(ServiceOptions.DefaultTimeoutSeconds, (int)d.RequestTimeout.TotalSeconds);

        ServiceOptions o = From(
            $"{ServiceOptions.AuthVar}=none",
            $"{ServiceOptions.MaxQuestionCharsVar}=500",
            $"{ServiceOptions.TimeoutSecondsVar}=30");
        Assert.Equal(500, o.MaxQuestionChars);
        Assert.Equal(TimeSpan.FromSeconds(30), o.RequestTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("soon")]
    public void NonsenseLimitsRaiseRatherThanFallBackQuietly(string value)
    {
        var e = Assert.Throws<EnvFile.EnvFileException>(
            () => From($"{ServiceOptions.AuthVar}=none", $"{ServiceOptions.TimeoutSecondsVar}={value}"));
        Assert.Contains(ServiceOptions.TimeoutSecondsVar, e.Message);
    }

    [Fact]
    public void TheDebugTranscriptIsOffUnlessAskedFor()
    {
        // Off is the public-traffic posture, so it is the default — and the
        // rendered options must say the state, because /healthz and the boot
        // line both lean on being able to see it.
        ServiceOptions off = From($"{ServiceOptions.AuthVar}=none");
        Assert.False(off.DebugTranscript);
        Assert.Contains("debug_transcript=off", off.ToString());

        ServiceOptions on = From(
            $"{ServiceOptions.AuthVar}=none",
            $"{ServiceOptions.DebugTranscriptVar}=1");
        Assert.True(on.DebugTranscript);
        Assert.Contains("debug_transcript=on", on.ToString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("ON")]
    [InlineData("yes")]
    public void RecognizedTrueSpellingsTurnItOn(string value)
    {
        ServiceOptions o = From(
            $"{ServiceOptions.AuthVar}=none",
            $"{ServiceOptions.DebugTranscriptVar}={value}");
        Assert.True(o.DebugTranscript);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    public void RecognizedFalseSpellingsTurnItOff(string value)
    {
        ServiceOptions o = From(
            $"{ServiceOptions.AuthVar}=none",
            $"{ServiceOptions.DebugTranscriptVar}={value}");
        Assert.False(o.DebugTranscript);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("enabled")]
    public void ANonsenseTranscriptValueRaisesRatherThanGuesses(string value)
    {
        var e = Assert.Throws<EnvFile.EnvFileException>(() => From(
            $"{ServiceOptions.AuthVar}=none",
            $"{ServiceOptions.DebugTranscriptVar}={value}"));
        Assert.Contains(ServiceOptions.DebugTranscriptVar, e.Message);
    }

    [Fact]
    public void RejectAnswersWithAReasonOrNull()
    {
        ServiceOptions o = From($"{ServiceOptions.AuthVar}=none",
            $"{ServiceOptions.MaxQuestionCharsVar}=20");

        Assert.Null(o.Reject(new AskRequest { Question = "how much creatine?" }));
        Assert.Contains("required", o.Reject(new AskRequest { Question = "   " })!);
        // Over the limit is a 400, never a truncation: truncating changes the
        // question, and the answer would be to something nobody asked.
        Assert.Contains("20 character", o.Reject(new AskRequest { Question = new string('x', 21) })!);
        Assert.Contains("top", o.Reject(new AskRequest { Question = "q?", Top = 0 })!);
        Assert.Contains("top", o.Reject(
            new AskRequest { Question = "q?", Top = ServiceOptions.MaxTop + 1 })!);
        Assert.Null(o.Reject(new AskRequest { Question = "q?", Top = ServiceOptions.MaxTop }));
        // The history check is the same one that was already a 400 (item 18).
        Assert.Contains("role", o.Reject(new AskRequest
        {
            Question = "q?",
            History = [new AskHistoryTurn { Role = "system", Text = "…" }],
        })!);
    }

    [Fact]
    public void TheSupportRouteComesFromTheRuntimeContract()
    {
        ServiceOptions on = ServiceOptions.FromValues(
            EnvFile.Parse($"{ServiceOptions.AuthVar}=none"), Runtime());
        Assert.Equal(Prompts.DefaultSupportContact, on.SupportContact);

        ServiceOptions off = ServiceOptions.FromValues(
            EnvFile.Parse($"{ServiceOptions.AuthVar}=none"), Runtime(supportContact: null));
        Assert.Null(off.SupportContact);
    }

    [Fact]
    public void TheSecretIsNeverRendered()
    {
        ServiceOptions o = From($"{ServiceOptions.ApiKeyVar}={Secret}");
        string shown = o.ToString();
        Assert.DoesNotContain(Secret, shown);
        Assert.Contains("shared-secret***", shown);
    }
}
