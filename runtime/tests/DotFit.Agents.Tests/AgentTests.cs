using DotFit.Agents.Guardrails;
using DotFit.Agents.Rewrite;
using DotFit.Agents.Structured;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agents.Tests;

public class AgentTests
{
    private static AIAgent AgentOver(ScriptedChatClient client, string instructions = "test instructions") =>
        new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatOptions = new ChatOptions { Instructions = instructions },
        });

    [Fact]
    public async Task StructuredCallPassesStrictSchemaAndDeserializes()
    {
        var client = new ScriptedChatClient(
            """{"escalate":false,"reasons":[],"claim_trap":false,"notes":"none"}""");
        GuardrailVerdict verdict = await StructuredCall.RunAsync<GuardrailVerdict>(
            AgentOver(client), "question", "dotfit_guardrail", Schemas.Guardrail);

        Assert.False(verdict.Escalate);
        var call = Assert.Single(client.Calls);
        Assert.Equal("question", call.Messages.Single(m => m.Role == ChatRole.User).Text);
        Assert.Equal("dotfit_guardrail", Assert.IsType<ChatResponseFormatJson>(call.Options?.ResponseFormat).SchemaName);
    }

    [Fact]
    public async Task StructuredCallRejectsInvalidJson()
    {
        var client = new ScriptedChatClient("definitely not json");
        await Assert.ThrowsAsync<StructuredCallException>(() => StructuredCall.RunAsync<GuardrailVerdict>(
            AgentOver(client), "q", "dotfit_guardrail", Schemas.Guardrail));
    }

    [Fact]
    public async Task GuardrailDegradesOpenOnBadReply()
    {
        var client = new ScriptedChatClient("garbage");
        var guardrail = new AgentGuardrail(AgentOver(client));
        GuardrailVerdict verdict = await guardrail.CheckAsync("is this safe?");
        Assert.True(verdict.Degraded);
        Assert.False(verdict.Escalate); // fail-open: the answer agent still carries the policy
    }

    [Fact]
    public async Task GuardrailParsesEscalation()
    {
        var client = new ScriptedChatClient(
            """{"escalate":true,"reasons":["under_18"],"claim_trap":false,"notes":"child dosing"}""");
        var guardrail = new AgentGuardrail(AgentOver(client));
        GuardrailVerdict verdict = await guardrail.CheckAsync("how much for my 10 year old?");
        Assert.True(verdict.Escalate);
        Assert.Equal(["guidance for someone under 18"], verdict.DisplayReasons);
    }

    [Fact]
    public async Task RewriterParsesMentionsAndSendsKnownFamilies()
    {
        var client = new ScriptedChatClient(
            """{"canonical_question":"canonical q","product_mentions":["Test Family"],"topics":["t"],"confidence":0.9}""");
        var rewriter = new AgentQueryRewriter(AgentOver(client), ["Test Family", "Other Family"]);
        RewriteResult rewrite = await rewriter.RewriteAsync("test q?", ConversationHistory.Empty);

        Assert.Equal("canonical q", rewrite.CanonicalQuestion);
        Assert.Equal(["Test Family"], rewrite.ProductMentions);
        string user = client.Calls.Single().Messages.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Known dotFIT product families: Test Family; Other Family", user);
    }

    [Fact]
    public async Task RewriterDegradesToRawQuestion()
    {
        var client = new ScriptedChatClient("garbage");
        var rewriter = new AgentQueryRewriter(AgentOver(client), ["Test Family"]);
        RewriteResult rewrite = await rewriter.RewriteAsync("raw question", ConversationHistory.Empty);
        Assert.True(rewrite.Degraded);
        Assert.Equal("raw question", rewrite.CanonicalQuestion);
        Assert.Empty(rewrite.ProductMentions);
    }
}
