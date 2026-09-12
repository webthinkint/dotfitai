using DotFit.Agentic.Config;
using DotFit.Agentic.Turn;
using DotFit.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotFit.Agentic.Tests;

/// <summary>
/// The loop's shapes (design §11): the handful of behaviours that would fail
/// silently if they broke. Not a correctness suite for the model — the model is
/// not deterministic and no test here pretends otherwise.
/// </summary>
public class LoopTests
{
    private static AgenticAssistant Build(
        ScriptedChatClient client,
        out FakeSearch search,
        AgenticOptions? options = null)
    {
        search = new FakeSearch(
            Fixtures.Document(id: "pdsrg-example-001", title: "Dosing"),
            Fixtures.Document(id: "pdsrg-example-002", title: "Mechanism"));
        var store = new FakeDocumentStore();
        AIAgent agent = client.AsAIAgent(instructions: "test instructions", name: "test");
        return new AgenticAssistant(agent, search, store, Fixtures.Aliases(), options ?? new AgenticOptions());
    }

    private static async Task<List<TurnEvent>> RunAsync(AgenticAssistant assistant, AskRequest request)
    {
        var events = new List<TurnEvent>();
        await foreach (TurnEvent turnEvent in assistant.AskAsync(request))
            events.Add(turnEvent);
        return events;
    }

    [Fact]
    public async Task A_turn_that_needs_no_lookup_calls_no_tool()
    {
        // The whole reason small talk needs no special branch on this branch
        // (v1 needed one — its item 25). A greeting is just a turn with no call.
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Hey! What can I help with?"));
        AgenticAssistant assistant = Build(client, out FakeSearch search);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "hi there" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Empty(search.Queries);
        Assert.Empty(result.ToolCalls);
        Assert.Empty(result.Sources);
        Assert.Equal("Hey! What can I help with?", result.AnswerText);
    }

    [Fact]
    public async Task Every_source_is_published_before_any_delta_that_could_cite_it()
    {
        // The §7 ordering contract, and the reason [n] survives live streaming.
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("c1", "search", new { query = "creatine dosing" }),
            ScriptedChatClient.Text("Take 5 g daily [1]."));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "how much creatine?" });

        int lastSource = events.FindLastIndex(e => e is TurnSourceEvent);
        int firstDelta = events.FindIndex(e => e is TurnDeltaEvent);
        Assert.True(lastSource >= 0, "no source events were emitted");
        Assert.True(firstDelta > lastSource,
            "a delta was emitted before a source it could cite — the [n] contract is broken");
    }

    [Fact]
    public async Task A_search_stage_carries_what_the_model_searched_for()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("c1", "search", new { query = "creatine dosing" }),
            ScriptedChatClient.Text("Take 5 g daily [1]."));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "how much creatine?" });
        TurnStageEvent stage = events.OfType<TurnStageEvent>().Single(e => e.Stage == Stages.Search);

        Assert.Equal("creatine dosing", stage.Detail);
    }

    [Fact]
    public async Task The_result_records_the_tools_the_sources_and_what_was_cited()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("c1", "search", new { query = "creatine dosing" }),
            ScriptedChatClient.Text("Take 5 g daily [1]."));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "how much creatine?" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Single(result.ToolCalls);
        Assert.Equal(Stages.Search, result.ToolCalls[0].Tool);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal([1], result.CitedSources);
        Assert.False(result.BudgetExhausted);
    }

    [Fact]
    public async Task Two_searches_keep_counting_rather_than_restarting_at_one()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("c1", "search", new { query = "first" }),
            ScriptedChatClient.Call("c2", "search", new { query = "second" }),
            ScriptedChatClient.Text("Both [1] and [3]."));
        var search = new FakeSearch();
        var store = new FakeDocumentStore();
        int call = 0;
        search.Handler = _ => call++ == 0
            ? [Fixtures.Document(id: "a"), Fixtures.Document(id: "b")]
            : [Fixtures.Document(id: "c")];
        AIAgent agent = client.AsAIAgent(instructions: "test", name: "test");
        var assistant = new AgenticAssistant(agent, search, store, Fixtures.Aliases());

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "q" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Equal([1, 2, 3], result.Sources.Select(s => s.N));
        Assert.Equal("c", result.Sources[2].Id);
    }

    [Fact]
    public async Task The_budget_ends_tool_use_in_a_tool_result_not_an_exception()
    {
        // §6: a model told to stop finishes its turn in a sentence. A model
        // whose tool throws produces an error the customer sees.
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("c1", "search", new { query = "one" }),
            ScriptedChatClient.Call("c2", "search", new { query = "two" }),
            ScriptedChatClient.Text("Here is what I found [1]."));
        AgenticAssistant assistant = Build(client, out _, new AgenticOptions { MaxToolCalls = 1 });

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "q" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.True(result.BudgetExhausted);
        Assert.Equal("Here is what I found [1].", result.AnswerText);
        Assert.NotNull(result.ToolCalls[1].Refusal);
    }

    [Fact]
    public async Task History_reaches_the_model_as_plain_turns()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Sure."));
        AgenticAssistant assistant = Build(client, out _);

        await RunAsync(assistant, new AskRequest
        {
            Question = "and for someone smaller?",
            History =
            [
                new ConversationTurn(ConversationRole.User, "how much creatine?"),
                new ConversationTurn(ConversationRole.Assistant, "5 g daily [1]."),
            ],
        });

        List<ChatMessage> sent = client.Calls[0];
        // Instructions are the agent's, so the message list is history + question.
        Assert.Equal("how much creatine?", sent[^3].Text);
        Assert.Equal(ChatRole.Assistant, sent[^2].Role);
        Assert.Equal("and for someone smaller?", sent[^1].Text);
    }

    [Fact]
    public async Task A_trailing_echo_of_the_question_is_dropped_from_history()
    {
        // Defensive: a relay that appends the turn to its transcript before
        // calling us would otherwise send the question as its own context.
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Sure."));
        AgenticAssistant assistant = Build(client, out _);

        await RunAsync(assistant, new AskRequest
        {
            Question = "how much creatine?",
            History = [new ConversationTurn(ConversationRole.User, "how much creatine?")],
        });

        Assert.Single(client.Calls[0], m => m.Text == "how much creatine?");
    }

    [Fact]
    public async Task A_failure_ends_in_a_handoff_and_still_produces_a_result()
    {
        var client = new ScriptedChatClient { Throw = new InvalidOperationException("deployment exploded") };
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "q" });

        TurnErrorEvent error = events.OfType<TurnErrorEvent>().Single();
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;
        Assert.Equal(error.HandoffText, result.AnswerText);
        Assert.Equal(error.HandoffText, events.OfType<TurnDeltaEvent>().Last().Text);
        Assert.IsType<TurnResultEvent>(events[^1]);
    }

    [Fact]
    public async Task An_empty_completion_still_says_something()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text(""));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "q" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Equal(assistant.EmptyAnswerMessage, result.AnswerText);
    }

    [Fact]
    public async Task Nothing_is_withheld_when_an_answer_cites_nothing()
    {
        // The v1 behaviour this branch removed: an uncited answer was withheld
        // on citation_presence and the customer got a support handoff instead.
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Generally, protein helps recovery."));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "does protein help?" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Equal("Generally, protein helps recovery.", result.AnswerText);
        Assert.Empty(result.CitedSources);
        Assert.Empty(events.OfType<TurnErrorEvent>());
    }
}
