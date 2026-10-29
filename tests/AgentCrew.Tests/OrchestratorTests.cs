using AgentCrew.Core.Agents;
using AgentCrew.Core.Events;
using AgentCrew.Core.Llm;
using AgentCrew.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace AgentCrew.Tests;

public class JsonExtractorTests
{
    private sealed record Sample(string Name, int Value);

    [Theory]
    [InlineData("""{"name":"a","value":1}""")]
    [InlineData("```json\n{\"name\": \"a\", \"value\": 1}\n```")]
    [InlineData("Вот план: {\"name\": \"a\", \"value\": 1} — готово")]
    [InlineData("{\"name\": \"a } {\", \"value\": 1}")]
    public void Extracts_json_object_from_model_output(string text)
    {
        Assert.True(JsonExtractor.TryParse<Sample>(text, out var sample));
        Assert.Equal(1, sample!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("нет json")]
    [InlineData("{ broken")]
    public void Returns_false_for_garbage(string text) =>
        Assert.False(JsonExtractor.TryParse<Sample>(text, out _));
}

public class OrchestratorTests
{
    private static CrewOrchestrator Crew(IChatClient chat) =>
        new(chat, CrewFactory.CreateDefault(TestData.Knowledge(), TestData.Sales()));

    [Fact]
    public void Plan_parsing_drops_unknown_dependencies_and_fixes_agents()
    {
        var plan = Crew(new DemoChatClient(TimeSpan.Zero)).ParsePlan("""
            {"goal": "g", "steps": [
              {"id": "s1", "agent": "analyst", "task": "a", "dependsOn": ["s2"]},
              {"id": "s2", "agent": "hacker", "task": "b", "dependsOn": ["s1", "s9"]},
              {"id": "s3", "agent": "researcher", "task": "", "dependsOn": []}
            ]}
            """, "task");

        Assert.Equal(2, plan.Steps.Count);
        Assert.Empty(plan.Steps[0].DependsOn); // ссылка вперёд → убрана (защита от циклов)
        Assert.Equal(["s1"], plan.Steps[1].DependsOn);
        Assert.Equal("researcher", plan.Steps[1].Agent); // неизвестный агент → исполнитель по умолчанию
    }

    [Fact]
    public void Invalid_plan_falls_back_to_all_workers()
    {
        var plan = Crew(new DemoChatClient(TimeSpan.Zero)).ParsePlan("не JSON", "вопрос");

        Assert.Equal(["researcher", "analyst"], plan.Steps.Select(s => s.Agent));
        Assert.All(plan.Steps, s => Assert.Equal("вопрос", s.Task));
    }

    [Fact]
    public async Task Full_run_plans_executes_reviews_and_revises()
    {
        var sink = new CollectingEventSink();

        var result = await Crew(new DemoChatClient(TimeSpan.Zero)).RunAsync(
            new CrewRequest("Какие товары выросли по выручке и какие рекомендации?"), sink);

        // План: аналитик и исследователь параллельно + зависимый шаг исследователя.
        Assert.Equal(3, result.Plan.Steps.Count);
        Assert.All(result.Steps, s => Assert.True(s.Succeeded));
        Assert.Contains("Умные часы", result.Steps[0].Output);

        // Критик сначала отклонил, затем одобрил доработанную версию.
        Assert.Equal([false, true], result.Reviews.Select(r => r.Approved));
        Assert.Contains("## Рекомендации", result.Answer);
        Assert.Contains("Умные часы", result.Answer);

        var types = sink.Events.Select(e => e.Type).ToList();
        Assert.Equal(AgentEventType.RunStarted, types[0]);
        Assert.Equal(AgentEventType.RunCompleted, types[^1]);
        Assert.Contains(AgentEventType.PlanCreated, types);
        Assert.Contains(AgentEventType.ToolCall, types);
        Assert.Equal(2, types.Count(t => t == AgentEventType.CriticReview));
        // 2 поиска исследователя + 2 запроса к данным и калькулятор у аналитика.
        Assert.Equal(5, result.Usage.ToolCalls);
        Assert.Contains("«Умные часы» — лидера роста (+40,0%)", result.Answer);
        Assert.Contains("«Ноутбуки» (-10,0%)", result.Answer);
        Assert.Contains("в регионе «Москва» (+10,0%)", result.Answer);
    }

    [Fact]
    public async Task Small_talk_skips_workers_and_critic()
    {
        var result = await Crew(new DemoChatClient(TimeSpan.Zero)).RunAsync(new CrewRequest("Привет!"));

        Assert.Empty(result.Plan.Steps);
        Assert.Empty(result.Reviews);
        Assert.Contains("Могу", result.Answer);
    }

    [Fact]
    public async Task Failed_step_does_not_break_the_run()
    {
        var chat = new FailingAnalystClient();

        var result = await Crew(chat).RunAsync(new CrewRequest("Динамика выручки по товарам"));

        Assert.Contains(result.Steps, s => !s.Succeeded && s.Step.Agent == "analyst");
        Assert.Contains(result.Steps, s => s.Succeeded && s.Step.Agent == "researcher");
        Assert.False(string.IsNullOrWhiteSpace(result.Answer));
    }

    [Fact]
    public async Task Review_loop_stops_when_revision_makes_no_progress()
    {
        var crew = new CrewOrchestrator(new StubbornWriterClient(), CrewFactory.CreateDefault(TestData.Knowledge(), TestData.Sales()),
            new CrewOptions { MaxRevisions = 5 });

        var result = await crew.RunAsync(new CrewRequest("Динамика выручки по товарам"));

        // Редактор каждый раз выдумывает одно и то же число: после второго одинакового вердикта цикл прекращается.
        Assert.Equal(2, result.Reviews.Count);
        Assert.All(result.Reviews, r => Assert.False(r.Approved));
    }

    /// <summary>Демо-модель, в которой редактор всегда вставляет неподтверждённое число.</summary>
    private sealed class StubbornWriterClient : DelegatingChatClient
    {
        public StubbornWriterClient() : base(new DemoChatClient(TimeSpan.Zero))
        {
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            messages.First().Text.Contains("agent_id: writer")
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "Выручка выросла на 77%.\n\n## Рекомендации\n1. Расти дальше."))
                : await base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <summary>Демо-модель, у которой «падает» провайдер при обращении аналитика.</summary>
    private sealed class FailingAnalystClient : DelegatingChatClient
    {
        public FailingAnalystClient() : base(new DemoChatClient(TimeSpan.Zero))
        {
        }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            messages.First().Text.Contains("agent_id: analyst")
                ? throw new HttpRequestException("provider unavailable")
                : base.GetResponseAsync(messages, options, cancellationToken);
    }
}

public class FactCheckerTests
{
    private const string Sources = "| Умные часы | 40 611 400 | 54 182 400 | +33,4% |\nСредний чек растёт на 15–20%. План: 2026-Q3.";

    [Theory]
    [InlineData("Умные часы выросли на 33,4% (54 182 400 ₽).")]
    [InlineData("Рост около 33% в 2026 году")] // округление процента допустимо
    [InlineData("Чек растёт на 15–20%")]
    [InlineData("Выручка 54 182 400 ₽")]
    public void Accepts_numbers_present_in_sources(string draft) =>
        Assert.Empty(FactChecker.FindUnsupportedNumbers(draft, Sources));

    [Fact]
    public void Flags_invented_numbers()
    {
        var unsupported = FactChecker.FindUnsupportedNumbers("Рост 45% и выручка 60 000 000 ₽, но 3 товара в 2025 году", Sources);

        Assert.Equal(["45%", "60 000 000"], unsupported);
    }

    [Fact]
    public void Number_check_overrides_critic_approval()
    {
        var verdict = CrewOrchestrator.CheckGrounding(new CriticVerdict(true, 9, [], "ок"), "Рост 45%", Sources);

        Assert.False(verdict.Approved);
        Assert.Equal(6, verdict.Score);
        Assert.Contains("45%", verdict.Issues.Single());
    }

    [Fact]
    public void Flags_citations_to_documents_absent_from_tool_results()
    {
        const string toolOutput = "[market-report-q3 › Тренды] (релевантность 3.1)\nСпрос растёт.";

        var unsupported = FactChecker.FindUnsupportedCitations(
            "Спрос растёт [market-report-q3 › Тренды], см. также [документ › ключевые цифры].", toolOutput);

        Assert.Equal(["[документ › ключевые цифры]"], unsupported);
    }

    [Theory]
    [InlineData("### Итоговый ответ\n\nТекст", "Текст")]
    [InlineData("## Ответ:\nТекст", "Текст")]
    [InlineData("## Рекомендации\nТекст", "## Рекомендации\nТекст")]
    public void Normalize_strips_service_heading(string input, string expected) =>
        Assert.Equal(expected, CrewOrchestrator.Normalize(input));
}
