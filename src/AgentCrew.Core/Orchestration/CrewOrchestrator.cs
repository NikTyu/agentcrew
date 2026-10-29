using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentCrew.Core.Agents;
using AgentCrew.Core.Events;
using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Orchestration;

/// <summary>
/// Оркестратор команды агентов:
/// 1) планировщик раскладывает запрос в граф шагов (JSON);
/// 2) независимые шаги выполняются исполнителями параллельно, зависимые — получают результаты предшественников;
/// 3) редактор пишет ответ, критик проверяет его, и при замечаниях ответ уходит на доработку (reflection loop).
/// </summary>
public sealed class CrewOrchestrator(IChatClient chatClient, AgentCatalog catalog, CrewOptions? options = null)
{
    public const string OrchestratorName = "orchestrator";

    private const string AutoCheckPrefix = "Автопроверка:";

    private readonly CrewOptions _options = options ?? new CrewOptions();

    public AgentCatalog Catalog => catalog;

    public async Task<CrewResult> RunAsync(CrewRequest request, IAgentEventSink? sink = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Task);
        sink ??= NullEventSink.Instance;

        var stopwatch = Stopwatch.StartNew();
        var usage = new UsageTracker();
        var runner = new AgentRunner(chatClient, sink, usage, _options.Runtime);

        await sink.EmitAsync(new AgentEvent(AgentEventType.RunStarted, OrchestratorName, request.Task), cancellationToken);

        var plan = await CreatePlanAsync(runner, request, sink, cancellationToken);
        var steps = await ExecutePlanAsync(runner, request, plan, sink, cancellationToken);
        var (answer, reviews) = await WriteAndReviewAsync(runner, request, steps, sink, cancellationToken);

        await sink.EmitAsync(new AgentEvent(AgentEventType.FinalAnswer, catalog.Writer.Id, answer), cancellationToken);

        var result = new CrewResult(answer, plan, steps, reviews, usage.Snapshot(), stopwatch.Elapsed);
        await sink.EmitAsync(new AgentEvent(AgentEventType.RunCompleted, OrchestratorName, null, new
        {
            durationMs = (long)result.Duration.TotalMilliseconds,
            steps = steps.Count,
            revisions = Math.Max(0, reviews.Count - 1),
            approved = reviews.Count == 0 || reviews[^1].Approved,
            usage = result.Usage.PerAgent,
            totals = new
            {
                llmCalls = result.Usage.LlmCalls,
                inputTokens = result.Usage.InputTokens,
                outputTokens = result.Usage.OutputTokens,
                toolCalls = result.Usage.ToolCalls,
            },
        }), cancellationToken);

        return result;
    }

    // ---------- 1. Планирование ----------

    private async Task<Plan> CreatePlanAsync(AgentRunner runner, CrewRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        var input = new StringBuilder()
            .AppendLine($"{CrewMarkers.Request} {request.Task}")
            .Append(FormatHistory(request.History))
            .ToString();

        var raw = await runner.RunAsync(catalog.Planner, input, cancellationToken: ct);
        var plan = ParsePlan(raw, request.Task);

        await sink.EmitAsync(new AgentEvent(AgentEventType.PlanCreated, catalog.Planner.Id, plan.Goal, plan), ct);
        return plan;
    }

    internal Plan ParsePlan(string raw, string task)
    {
        if (!JsonExtractor.TryParse<PlanDto>(raw, out var dto) || dto!.Steps is null)
        {
            // Модель не вернула валидный план — деградируем до «каждый исполнитель смотрит на запрос целиком».
            return new Plan(task, [.. catalog.Workers.Values.Select((w, i) => new PlanStep($"s{i + 1}", w.Id, task, []))]);
        }

        var steps = new List<PlanStep>();
        foreach (var s in dto.Steps.Take(_options.MaxPlanSteps))
        {
            if (string.IsNullOrWhiteSpace(s.Task))
            {
                continue;
            }

            var agent = s.Agent is not null && catalog.Workers.TryGetValue(s.Agent, out var known)
                ? known.Id
                : catalog.Workers.Keys.First();
            var id = string.IsNullOrWhiteSpace(s.Id) || steps.Any(x => x.Id == s.Id) ? $"s{steps.Count + 1}" : s.Id.Trim();
            steps.Add(new PlanStep(id, agent, s.Task.Trim(), [.. (s.DependsOn ?? []).Where(d => !string.IsNullOrWhiteSpace(d))]));
        }

        // Оставляем только ссылки на существующие и более ранние шаги — это исключает циклы.
        var validated = new List<PlanStep>();
        foreach (var step in steps)
        {
            var earlier = validated.Select(v => v.Id).ToHashSet();
            validated.Add(step with { DependsOn = [.. step.DependsOn.Where(earlier.Contains).Distinct()] });
        }

        return new Plan(string.IsNullOrWhiteSpace(dto.Goal) ? task : dto.Goal.Trim(), validated);
    }

    // ---------- 2. Выполнение графа шагов ----------

    private async Task<IReadOnlyList<StepResult>> ExecutePlanAsync(AgentRunner runner, CrewRequest request, Plan plan, IAgentEventSink sink, CancellationToken ct)
    {
        var results = new ConcurrentDictionary<string, StepResult>();
        var pending = plan.Steps.ToList();

        while (pending.Count > 0)
        {
            // «Волна» — все шаги, зависимости которых уже выполнены. Они идут параллельно.
            var wave = pending.Where(s => s.DependsOn.All(results.ContainsKey)).ToList();
            if (wave.Count == 0)
            {
                wave = [pending[0]];
            }

            await Task.WhenAll(wave.Select(async step =>
            {
                results[step.Id] = await RunStepAsync(runner, request, plan, step, results, sink, ct);
            }));
            pending.RemoveAll(wave.Contains);
        }

        return [.. plan.Steps.Select(s => results[s.Id])];
    }

    private async Task<StepResult> RunStepAsync(
        AgentRunner runner, CrewRequest request, Plan plan, PlanStep step,
        IReadOnlyDictionary<string, StepResult> done, IAgentEventSink sink, CancellationToken ct)
    {
        var agent = catalog.Workers[step.Agent];
        await sink.EmitAsync(new AgentEvent(AgentEventType.StepStarted, agent.Id, step.Task) { StepId = step.Id }, ct);

        var input = new StringBuilder()
            .AppendLine($"{CrewMarkers.Request} {request.Task}")
            .AppendLine($"{CrewMarkers.Goal} {plan.Goal}")
            .AppendLine($"{CrewMarkers.Task} {step.Task}");

        var dependencies = step.DependsOn.Where(done.ContainsKey).Select(id => done[id]).ToList();
        if (dependencies.Count > 0)
        {
            input.AppendLine().AppendLine(CrewMarkers.Dependencies);
            foreach (var dep in dependencies)
            {
                input.AppendLine($"### {dep.Step.Id} · {dep.Step.Agent}").AppendLine(dep.Output).AppendLine();
            }
        }

        var stopwatch = Stopwatch.StartNew();
        StepResult result;
        try
        {
            var output = await runner.RunAsync(agent, input.ToString(), step.Id, ct);
            result = new StepResult(step, output, true, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Падение одного шага не роняет весь прогон: редактор увидит, что данных нет.
            await sink.EmitAsync(new AgentEvent(AgentEventType.Error, agent.Id, ex.Message) { StepId = step.Id }, ct);
            result = new StepResult(step, $"Шаг не выполнен: {ex.Message}", false, stopwatch.Elapsed);
        }

        await sink.EmitAsync(new AgentEvent(AgentEventType.StepCompleted, agent.Id, null, new
        {
            succeeded = result.Succeeded,
            durationMs = (long)result.Duration.TotalMilliseconds,
        })
        {
            StepId = step.Id,
        }, ct);
        return result;
    }

    // ---------- 3. Ответ и рефлексия ----------

    private async Task<(string Answer, IReadOnlyList<CriticVerdict> Reviews)> WriteAndReviewAsync(
        AgentRunner runner, CrewRequest request, IReadOnlyList<StepResult> steps, IAgentEventSink sink, CancellationToken ct)
    {
        var context = FormatStepResults(steps);
        var reviews = new List<CriticVerdict>();
        string? feedback = null;
        string draft = "";

        for (var round = 0; round <= _options.MaxRevisions; round++)
        {
            var writerInput = new StringBuilder()
                .AppendLine($"{CrewMarkers.Request} {request.Task}")
                .Append(FormatHistory(request.History))
                .AppendLine()
                .AppendLine(CrewMarkers.Results)
                .AppendLine(steps.Count > 0 ? context : "(исполнители не привлекались — ответь напрямую)");
            if (feedback is not null)
            {
                writerInput.AppendLine().AppendLine(CrewMarkers.PreviousDraft).AppendLine(draft)
                    .AppendLine().AppendLine(CrewMarkers.Feedback).AppendLine(feedback);
            }

            draft = Normalize(await runner.RunAsync(catalog.Writer, writerInput.ToString(), cancellationToken: ct));

            // Простые реплики (приветствие, вопрос о возможностях) не требуют фактчекинга.
            if (steps.Count == 0)
            {
                break;
            }

            // Числа сверяем с результатами инструментов, а не с ответами агентов: те тоже могут ошибаться.
            var sources = string.Join("\n", runner.ToolOutputs) + "\n" + request.Task + "\n" + FormatHistory(request.History);
            var verdict = CheckGrounding(await ReviewAsync(runner, request, context, draft, ct), draft, sources);
            reviews.Add(verdict);
            await sink.EmitAsync(new AgentEvent(AgentEventType.CriticReview, catalog.Critic.Id, verdict.Feedback, verdict), ct);

            if (verdict.Approved)
            {
                break;
            }

            // Доработка не устранила замечания автопроверки — следующие раунды лишь потратят время и токены.
            // Сравниваем только детерминированные замечания: формулировки LLM-критика меняются от раунда к раунду.
            if (reviews.Count > 1 && AutoIssues(verdict).Any() && AutoIssues(reviews[^2]).SequenceEqual(AutoIssues(verdict)))
            {
                await sink.EmitAsync(new AgentEvent(AgentEventType.AgentThinking, OrchestratorName,
                    "Доработка не устранила замечания — цикл ревью остановлен."), ct);
                break;
            }
            feedback = FormatFeedback(verdict);
        }

        return (draft, reviews);
    }

    private async Task<CriticVerdict> ReviewAsync(AgentRunner runner, CrewRequest request, string context, string draft, CancellationToken ct)
    {
        var input = new StringBuilder()
            .AppendLine($"{CrewMarkers.Request} {request.Task}")
            .AppendLine()
            .AppendLine(CrewMarkers.Results)
            .AppendLine(context)
            .AppendLine()
            .AppendLine(CrewMarkers.Draft)
            .AppendLine(draft)
            .ToString();

        var raw = await runner.RunAsync(catalog.Critic, input, cancellationToken: ct);
        if (!JsonExtractor.TryParse<CriticDto>(raw, out var dto))
        {
            // Непарсибельный ответ критика не должен блокировать выдачу результата.
            return new CriticVerdict(true, 0, [], "Критик вернул ответ не в формате JSON — черновик принят без замечаний.");
        }

        var score = (int)Math.Clamp(Math.Round(dto!.Score ?? 0), 0, 10);
        var approved = dto.Approved ?? score >= 8;
        return new CriticVerdict(approved, score, dto.Issues ?? [], dto.Feedback?.Trim() ?? "");
    }

    /// <summary>
    /// LLM-критик дополняется детерминированной сверкой с результатами инструментов:
    /// числа и ссылки на источники в черновике должны там присутствовать.
    /// </summary>
    internal static CriticVerdict CheckGrounding(CriticVerdict verdict, string draft, string sources)
    {
        var issues = new List<string>();
        var numbers = FactChecker.FindUnsupportedNumbers(draft, sources);
        if (numbers.Count > 0)
        {
            issues.Add($"{AutoCheckPrefix} числа не подтверждены данными — {string.Join(", ", numbers.Take(8))}. Используй только числа из результатов исполнителей.");
        }
        var citations = FactChecker.FindUnsupportedCitations(draft, sources);
        if (citations.Count > 0)
        {
            issues.Add($"{AutoCheckPrefix} несуществующие источники — {string.Join(", ", citations.Take(5))}. Ссылайся только на источники из результатов исполнителей.");
        }
        if (issues.Count == 0)
        {
            return verdict;
        }

        return verdict with
        {
            Approved = false,
            Score = Math.Min(verdict.Score, 6),
            Issues = [.. verdict.Issues, .. issues],
            Feedback = string.IsNullOrWhiteSpace(verdict.Feedback) || verdict.Approved ? string.Join(" ", issues) : verdict.Feedback,
        };
    }

    private static IEnumerable<string> AutoIssues(CriticVerdict verdict) =>
        verdict.Issues.Where(i => i.StartsWith(AutoCheckPrefix, StringComparison.Ordinal));

    /// <summary>Убирает служебный заголовок вроде «Итоговый ответ», который любят добавлять небольшие модели.</summary>
    internal static string Normalize(string answer)
    {
        var lines = answer.Trim().Split('\n').ToList();
        if (lines.Count > 1 && Regex.IsMatch(lines[0].Trim(), @"^#{1,6}\s*(Итоговый ответ|Ответ|Итог)\s*:?\s*$", RegexOptions.IgnoreCase))
        {
            lines.RemoveAt(0);
        }
        return string.Join('\n', lines).Trim();
    }

    private static string FormatFeedback(CriticVerdict verdict)
    {
        var sb = new StringBuilder();
        foreach (var issue in verdict.Issues)
        {
            sb.AppendLine($"- {issue}");
        }
        if (!string.IsNullOrWhiteSpace(verdict.Feedback))
        {
            sb.AppendLine(verdict.Feedback);
        }
        return sb.ToString().Trim();
    }

    private string FormatStepResults(IReadOnlyList<StepResult> steps)
    {
        var sb = new StringBuilder();
        foreach (var r in steps)
        {
            var name = catalog.Workers.TryGetValue(r.Step.Agent, out var a) ? a.DisplayName : r.Step.Agent;
            // Без идентификаторов шагов: иначе модели начинают ссылаться на «[s1]» вместо источников.
            sb.AppendLine($"### [{name}] {r.Step.Task}").AppendLine(r.Output).AppendLine();
        }
        return sb.ToString().Trim();
    }

    private string FormatHistory(IReadOnlyList<ChatTurn>? history)
    {
        if (history is null || history.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder().AppendLine().AppendLine(CrewMarkers.History);
        foreach (var turn in history.TakeLast(_options.HistoryTurns))
        {
            var who = turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "Пользователь" : "Ассистент";
            var content = turn.Content.Length > 1500 ? turn.Content[..1500] + "…" : turn.Content;
            sb.AppendLine($"{who}: {content}");
        }
        return sb.ToString();
    }

    private sealed record PlanDto(string? Goal, List<PlanStepDto>? Steps);

    private sealed record PlanStepDto(string? Id, string? Agent, string? Task, List<string>? DependsOn);

    private sealed record CriticDto(bool? Approved, double? Score, List<string>? Issues, string? Feedback);
}
