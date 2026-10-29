using AgentCrew.Core.Agents;
using AgentCrew.Core.Events;
using AgentCrew.Core.Tools;
using Microsoft.Extensions.AI;

namespace AgentCrew.Tests;

public class AgentRunnerTests
{
    private static AgentDefinition Analyst(int maxIterations = 4) => new()
    {
        Id = "analyst",
        DisplayName = "Аналитик",
        Description = "считает",
        Instructions = "Ты аналитик.",
        Tools = [CrewTools.Calculate()],
        MaxIterations = maxIterations,
    };

    [Fact]
    public async Task Calls_tool_and_feeds_result_back_to_model()
    {
        var chat = new TestData.ScriptedChatClient(
            TestData.ToolCall("c1", "calculator", new() { ["expression"] = "1500000 - 1000000" }),
            new ChatMessage(ChatRole.Assistant, "Прирост составил 500000 ₽."));
        var sink = new CollectingEventSink();
        var usage = new UsageTracker();

        var answer = await new AgentRunner(chat, sink, usage).RunAsync(Analyst(), "Посчитай прирост");

        Assert.Equal("Прирост составил 500000 ₽.", answer);
        var toolResult = chat.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("c1", toolResult.CallId);
        Assert.Equal("500000", toolResult.Result);

        Assert.Contains(sink.Events, e => e.Type == AgentEventType.ToolCall && e.Text == "calculator");
        Assert.Contains(sink.Events, e => e.Type == AgentEventType.ToolResult && e.Text == "500000");
        var stats = usage.Snapshot().PerAgent.Single();
        Assert.Equal(2, stats.LlmCalls);
        Assert.Equal(1, stats.ToolCalls);
        Assert.Equal(20, stats.InputTokens);
    }

    [Fact]
    public async Task Tool_errors_are_returned_to_model_instead_of_crashing()
    {
        var chat = new TestData.ScriptedChatClient(
            TestData.ToolCall("c1", "calculator", new() { ["expression"] = "1 / 0" }),
            TestData.ToolCall("c2", "web_search", new() { ["q"] = "x" }),
            new ChatMessage(ChatRole.Assistant, "Не удалось посчитать."));

        var answer = await new AgentRunner(chat, NullEventSink.Instance, new UsageTracker()).RunAsync(Analyst(), "...");

        Assert.Equal("Не удалось посчитать.", answer);
        var results = chat.Requests.Last().SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => (string)r.Result!).ToList();
        Assert.Contains("Деление на ноль", results[0]);
        Assert.Contains("недоступен", results[1]);
    }

    [Fact]
    public async Task Stops_tool_loop_after_max_iterations()
    {
        var loop = Enumerable.Range(0, 10)
            .Select(i => TestData.ToolCall($"c{i}", "calculator", new() { ["expression"] = "1+1" }))
            .ToArray();
        var chat = new TestData.ScriptedChatClient(loop);

        await new AgentRunner(chat, NullEventSink.Instance, new UsageTracker()).RunAsync(Analyst(maxIterations: 2), "...");

        // 2 итерации с инструментами + 1 финальный запрос без инструментов.
        Assert.Equal(3, chat.Requests.Count);
        Assert.Contains("Лимит вызовов", chat.Requests.Last().Last().Text);
    }

    [Fact]
    public async Task Runs_parallel_tool_calls_from_one_turn()
    {
        var chat = new TestData.ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("a", "calculator", new Dictionary<string, object?> { ["expression"] = "2*3" }),
                new FunctionCallContent("b", "calculator", new Dictionary<string, object?> { ["expression"] = "10-4" }),
            ]),
            new ChatMessage(ChatRole.Assistant, "6 и 6"));

        await new AgentRunner(chat, NullEventSink.Instance, new UsageTracker()).RunAsync(Analyst(), "...");

        var toolMessage = chat.Requests[1].Last();
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        Assert.Equal(["a", "b"], toolMessage.Contents.OfType<FunctionResultContent>().Select(r => r.CallId));
    }

    [Fact]
    public async Task Agent_is_sent_back_when_answer_has_numbers_not_from_tools()
    {
        var chat = new TestData.ScriptedChatClient(
            TestData.ToolCall("c1", "calculator", new() { ["expression"] = "1000 * 1.4" }),
            new ChatMessage(ChatRole.Assistant, "Выручка выросла на 25% до 1 234 567 ₽."),
            new ChatMessage(ChatRole.Assistant, "Выручка составила 1400 ₽ (рост 40%)."));
        var sink = new CollectingEventSink();
        var agent = Analyst() with { VerifyGrounding = true };

        var answer = await new AgentRunner(chat, sink, new UsageTracker()).RunAsync(agent, "Посчитай выручку при росте на 40%");

        Assert.Equal("Выручка составила 1400 ₽ (рост 40%).", answer);
        var note = chat.Requests[2].Last();
        Assert.Equal(ChatRole.User, note.Role);
        Assert.Contains("25%", note.Text);
        Assert.Contains("1 234 567", note.Text);
        Assert.Contains(sink.Events, e => e.Type == AgentEventType.AgentThinking && e.Text!.StartsWith("Автопроверка"));
    }

    [Fact]
    public async Task Grounding_retries_are_limited()
    {
        var chat = new TestData.ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant, "Рост 11%"),
            new ChatMessage(ChatRole.Assistant, "Рост 12%"),
            new ChatMessage(ChatRole.Assistant, "Рост 13%"));
        var agent = Analyst() with { VerifyGrounding = true };

        var answer = await new AgentRunner(chat, NullEventSink.Instance, new UsageTracker()).RunAsync(agent, "...");

        Assert.Equal("Рост 13%", answer); // после двух замечаний ответ принимается как есть
        Assert.Equal(3, chat.Requests.Count);
    }

    [Fact]
    public async Task Agent_with_tools_must_call_at_least_one()
    {
        var chat = new TestData.ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant, "Отвечаю сразу, без данных."),
            TestData.ToolCall("c1", "calculator", new() { ["expression"] = "2+2" }),
            new ChatMessage(ChatRole.Assistant, "Получилось 4."));

        var answer = await new AgentRunner(chat, NullEventSink.Instance, new UsageTracker())
            .RunAsync(Analyst() with { VerifyGrounding = true }, "...");

        Assert.Equal("Получилось 4.", answer);
        Assert.Contains("не вызвал ни одного инструмента", chat.Requests[1].Last().Text);
    }
}
