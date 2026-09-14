using DotFit.Agents.Cost;
using DotFit.Agentic.Config;
using DotFit.Agentic.Turn;
using DotFit.Agents;
using DotFit.Agents.Retrieval;
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
        IChatClient client,
        out FakeSearch search,
        AgenticOptions? options = null,
        PriceSheet? prices = null)
    {
        search = new FakeSearch(
            Fixtures.Document(id: "pdsrg-example-001", title: "Dosing"),
            Fixtures.Document(id: "pdsrg-example-002", title: "Mechanism"));
        var store = new FakeDocumentStore();
        AIAgent agent = client.AsAIAgent(instructions: "test instructions", name: "test");
        return new AgenticAssistant(
            agent, search, store, Fixtures.Aliases(), options ?? new AgenticOptions(), prices: prices);
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
    public async Task A_tools_stage_reaches_the_caller_while_the_tool_is_still_running()
    {
        // §9 sells stage.detail as the texture that stands in for a progress
        // bar, which it only is if it arrives during the wait. A loop that
        // drained on the model's next update could not: for a tool call that
        // update *is* the tool's own result, so "looking up creatine dosing"
        // landed once the lookup was done (open item 11).
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("c1", "search", new { query = "creatine dosing" }),
            ScriptedChatClient.Text("Take 5 g daily [1]."));
        AgenticAssistant assistant = Build(client, out FakeSearch search);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Gate = gate.Task;

        IAsyncEnumerator<TurnEvent> events =
            assistant.AskAsync(new AskRequest { Question = "how much creatine?" }).GetAsyncEnumerator();

        // The gate is never opened until after the stage is in hand, so without
        // the race this pull cannot complete — the loop would be waiting on the
        // model, the model on the tool, the tool on a gate nobody opens. The
        // timeout is what turns that deadlock into a readable failure.
        TurnStageEvent? stage = null;
        try
        {
            while (await Next(events))
            {
                if (events.Current is TurnStageEvent s && s.Stage == Stages.Search)
                {
                    stage = s;
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            // Deliberately not disposed on this path: disposing with the pull
            // still outstanding throws NotSupportedException and buries this.
            Assert.Fail("the search stage never arrived while the search was in flight — the loop is " +
                        "waiting on the model's next update, which is waiting on the tool (open item 11)");
        }

        Assert.NotNull(stage);
        Assert.Equal("creatine dosing", stage.Detail);
        Assert.Single(search.Queries);
        Assert.False(gate.Task.IsCompleted, "the search returned before the stage was observed");

        gate.SetResult();
        while (await Next(events))
        {
        }
        await events.DisposeAsync();
    }

    /// <summary>One pull, bounded, so a loop that cannot make progress fails instead of hanging.</summary>
    private static Task<bool> Next(IAsyncEnumerator<TurnEvent> events) =>
        events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

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
    public async Task A_failure_mid_answer_keeps_the_text_the_customer_already_read()
    {
        // The result is the record of the turn: the website team persists
        // `result.answer`. Replacing the partial text with the handoff stored
        // an answer nobody saw and lost the one they did — and took `cited`
        // with it, biasing the §8.3 record that stands in for a gate.
        var client = new ThrowsAfterDeltasChatClient(
            new InvalidOperationException("deployment exploded"), "Take 5 g daily", " with food");
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "how much creatine?" });
        TurnErrorEvent error = events.OfType<TurnErrorEvent>().Single();
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;
        string streamed = string.Concat(events.OfType<TurnDeltaEvent>().Select(d => d.Text));

        Assert.Equal(streamed, result.AnswerText);
        Assert.StartsWith("Take 5 g daily with food", result.AnswerText, StringComparison.Ordinal);
        Assert.EndsWith(error.HandoffText, result.AnswerText, StringComparison.Ordinal);
        // Appended with a break, not run on from the half-finished sentence.
        Assert.Contains("food\n\nSomething went wrong", result.AnswerText, StringComparison.Ordinal);
        Assert.IsType<TurnResultEvent>(events[^1]);
    }

    [Fact]
    public async Task Token_usage_is_summed_across_round_trips()
    {
        // Every round trip reports its own usage. Taking the last one dropped
        // the output tokens of every tool-calling round trip and reported the
        // last call's context as the input — understating exactly the expensive
        // turns the log exists to price (open item 5).
        var client = new ScriptedChatClient(
            [
                new FunctionCallContent("c1", "search",
                    new Dictionary<string, object?> { ["query"] = "creatine dosing" }),
                new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 }),
            ],
            [
                new TextContent("Take 5 g daily [1]."),
                new UsageContent(new UsageDetails { InputTokenCount = 500, OutputTokenCount = 20 }),
            ]);
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "how much creatine?" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Equal(600, result.InputTokens);
        Assert.Equal(30, result.OutputTokens);
    }

    [Fact]
    public async Task The_cost_block_prices_all_three_components_from_observed_usage()
    {
        // §9's `cost`: chat tokens off the model's own reports (cached input
        // kept apart — it is billed at a different rate), embedding tokens off
        // the search call's report, index queries off the calls the tool made.
        // Nothing is estimated and nothing is rounded — the arithmetic must be
        // exactly the sheet times the counts, or the owners are reading a
        // number nobody can stand behind.
        var sheet = new PriceSheet
        {
            Id = "test-sheet",
            ChatInputPerMillion = 1m,
            ChatCachedInputPerMillion = 0.1m,
            ChatOutputPerMillion = 10m,
            EmbeddingPerMillion = 0.2m,
            SearchPerThousand = 2m,
        };
        var client = new ScriptedChatClient(
            [
                new FunctionCallContent("c1", "search",
                    new Dictionary<string, object?> { ["query"] = "creatine dosing" }),
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 1_000, CachedInputTokenCount = 400, OutputTokenCount = 10,
                }),
            ],
            [
                new TextContent("Take 5 g daily [1]."),
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 2_000, CachedInputTokenCount = 1_000, OutputTokenCount = 20,
                }),
            ]);
        AgenticAssistant assistant = Build(client, out FakeSearch search, prices: sheet);
        search.Handler = p =>
        {
            p.UsageSink?.Embedding(31);   // what the embedding API would have reported
            return (IReadOnlyList<RetrievedDocument>)[Fixtures.Document(id: "pdsrg-example-001")];
        };

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "how much creatine?" });
        TurnCost cost = events.OfType<TurnResultEvent>().Single().Result.Cost!;

        Assert.Equal(3_000, cost.ChatInputTokens);
        Assert.Equal(1_400, cost.ChatCachedInputTokens);
        Assert.Equal(30, cost.ChatOutputTokens);
        Assert.Equal(1_600 * 1m / 1_000_000m + 1_400 * 0.1m / 1_000_000m + 30 * 10m / 1_000_000m, cost.ChatUsd);
        Assert.Equal(1, cost.EmbeddingCalls);
        Assert.Equal(31, cost.EmbeddingTokens);
        Assert.Equal(31 * 0.2m / 1_000_000m, cost.EmbeddingUsd);
        Assert.Equal(1, cost.IndexQueries);
        Assert.Equal(1 * 2m / 1_000m, cost.SearchUsd);
        Assert.Equal(cost.ChatUsd + cost.EmbeddingUsd + cost.SearchUsd, cost.TotalUsd);
        Assert.Equal("test-sheet", cost.PriceSheet);
    }

    [Fact]
    public async Task A_turn_with_no_usage_still_gets_a_zero_cost_block()
    {
        // The scripted fake reports no usage and calls no tool — small talk's
        // shape. The cost block exists and says zero, rather than being absent
        // and reading as "unknown".
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Hey!"));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "hi" });
        TurnCost cost = events.OfType<TurnResultEvent>().Single().Result.Cost!;

        Assert.Equal(0m, cost.TotalUsd);
        Assert.Equal(0, cost.IndexQueries);
        Assert.Equal(0, cost.EmbeddingCalls);
    }

    [Fact]
    public async Task Usage_stays_null_when_nothing_ever_reported_it()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Hey!"));
        AgenticAssistant assistant = Build(client, out _);

        List<TurnEvent> events = await RunAsync(assistant, new AskRequest { Question = "hi" });
        TurnResult result = events.OfType<TurnResultEvent>().Single().Result;

        Assert.Null(result.InputTokens);
        Assert.Null(result.OutputTokens);
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
