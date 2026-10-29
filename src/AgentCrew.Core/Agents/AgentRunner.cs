using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentCrew.Core.Events;
using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Agents;

/// <summary>
/// Исполняет одного агента по схеме ReAct: модель решает, какие инструменты вызвать,
/// рантайм вызывает их (параллельно, с таймаутом и перехватом ошибок), возвращает результаты
/// модели — и так до финального текстового ответа или исчерпания лимита итераций.
/// Цикл реализован вручную (а не через UseFunctionInvocation), чтобы публиковать
/// каждый шаг в трассу и контролировать лимиты.
/// </summary>
public sealed class AgentRunner(IChatClient chatClient, IAgentEventSink sink, UsageTracker usage, AgentRuntimeOptions? options = null)
{
    private readonly AgentRuntimeOptions _options = options ?? new AgentRuntimeOptions();
    private readonly ConcurrentQueue<string> _toolOutputs = new();

    /// <summary>Все результаты инструментов за время жизни раннера — «источник истины» для проверки чисел.</summary>
    public IReadOnlyCollection<string> ToolOutputs => _toolOutputs;

    public async Task<string> RunAsync(AgentDefinition agent, string input, string? stepId = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, agent.Instructions),
            new(ChatRole.User, input),
        ];

        var chatOptions = new ChatOptions { Temperature = _options.Temperature };
        if (agent.Tools.Count > 0)
        {
            chatOptions.Tools = [.. agent.Tools];
            chatOptions.ToolMode = ChatToolMode.Auto;
            chatOptions.AllowMultipleToolCalls = true;
        }
        if (agent.JsonOutput && _options.UseJsonMode)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.Json;
        }

        var observations = new List<string>();
        var groundingRetries = 0;

        for (var iteration = 1; iteration <= agent.MaxIterations; iteration++)
        {
            await EmitAsync(new AgentEvent(AgentEventType.AgentThinking, agent.Id, $"Итерация {iteration}") { StepId = stepId }, cancellationToken);

            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            usage.AddLlmCall(agent.Id, response.Usage);
            messages.AddRange(response.Messages);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                var answer = response.Text.Trim();
                if (agent.VerifyGrounding && groundingRetries < _options.MaxGroundingRetries && iteration < agent.MaxIterations)
                {
                    var issues = GroundingIssues(agent, answer, input, observations);
                    if (issues.Count > 0)
                    {
                        // Ответ не опирается на данные — возвращаем агента к работе с конкретным замечанием.
                        groundingRetries++;
                        var note = $"Автопроверка: {string.Join(" ", issues)} Не придумывай факты, цифры и источники.";
                        await EmitAsync(new AgentEvent(AgentEventType.AgentThinking, agent.Id, note) { StepId = stepId }, cancellationToken);
                        messages.Add(new ChatMessage(ChatRole.User, note));
                        continue;
                    }
                }

                await EmitAsync(new AgentEvent(AgentEventType.AgentMessage, agent.Id, answer) { StepId = stepId }, cancellationToken);
                return answer;
            }

            // Промежуточные рассуждения модели перед вызовом инструментов тоже показываем в трассе.
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                await EmitAsync(new AgentEvent(AgentEventType.AgentThinking, agent.Id, response.Text.Trim()) { StepId = stepId }, cancellationToken);
            }

            var results = await Task.WhenAll(calls.Select(call => InvokeToolAsync(agent, call, stepId, cancellationToken)));
            messages.Add(new ChatMessage(ChatRole.Tool, [.. results]));
            observations.AddRange(results.Select(r => r.Result as string ?? ""));
        }

        // Лимит исчерпан: просим модель подвести итог без инструментов, чтобы агент не зациклился.
        messages.Add(new ChatMessage(ChatRole.User,
            "Лимит вызовов инструментов исчерпан. Дай итоговый ответ на основе уже собранных данных."));
        chatOptions.Tools = null;
        chatOptions.ToolMode = null;

        var final = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        usage.AddLlmCall(agent.Id, final.Usage);
        var text = final.Text.Trim();
        await EmitAsync(new AgentEvent(AgentEventType.AgentMessage, agent.Id, text) { StepId = stepId }, cancellationToken);
        return text;
    }

    private async Task<FunctionResultContent> InvokeToolAsync(AgentDefinition agent, FunctionCallContent call, string? stepId, CancellationToken cancellationToken)
    {
        usage.AddToolCall(agent.Id);
        await EmitAsync(new AgentEvent(AgentEventType.ToolCall, agent.Id, call.Name, new { tool = call.Name, callId = call.CallId, arguments = call.Arguments })
        {
            StepId = stepId,
        }, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var tool = agent.Tools.FirstOrDefault(t => t.Name == call.Name);
        string resultText;
        var ok = false;

        if (tool is null)
        {
            // Ошибку возвращаем модели, а не бросаем исключение: так она может исправиться сама.
            resultText = $"Ошибка: инструмент '{call.Name}' недоступен. Доступные инструменты: {string.Join(", ", agent.Tools.Select(t => t.Name))}.";
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ToolTimeout);
            try
            {
                var result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments), timeout.Token);
                resultText = Truncate(Stringify(result), _options.MaxToolResultChars);
                ok = true;
                _toolOutputs.Enqueue(resultText);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                resultText = $"Ошибка: инструмент '{call.Name}' не ответил за {_options.ToolTimeout.TotalSeconds:0} с.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resultText = $"Ошибка выполнения инструмента '{call.Name}': {ex.Message}";
            }
        }

        await EmitAsync(new AgentEvent(AgentEventType.ToolResult, agent.Id, resultText, new
        {
            tool = call.Name,
            callId = call.CallId,
            ok,
            durationMs = stopwatch.ElapsedMilliseconds,
        })
        {
            StepId = stepId,
        }, cancellationToken);

        return new FunctionResultContent(call.CallId, resultText);
    }

    private static List<string> GroundingIssues(AgentDefinition agent, string answer, string input, List<string> observations)
    {
        var issues = new List<string>();
        if (agent.Tools.Count > 0 && observations.Count == 0)
        {
            issues.Add("Ты не вызвал ни одного инструмента — сначала получи данные через инструменты, затем отвечай.");
            return issues;
        }

        var sources = input + "\n" + string.Join("\n", observations);
        var numbers = FactChecker.FindUnsupportedNumbers(answer, sources);
        if (numbers.Count > 0)
        {
            issues.Add($"В ответе есть числа, которых нет в результатах инструментов: {string.Join(", ", numbers.Take(8))}. Получи точные данные через инструменты или убери эти числа.");
        }
        var citations = FactChecker.FindUnsupportedCitations(answer, sources);
        if (citations.Count > 0)
        {
            issues.Add($"Источники не найдены в результатах инструментов: {string.Join(", ", citations.Take(5))}. Ссылайся только на документы из результатов поиска.");
        }
        return issues;
    }

    private ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken) => sink.EmitAsync(evt, cancellationToken);

    internal static string Stringify(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => JsonSerializer.Serialize(result),
    };

    internal static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + $"\n…[обрезано, всего {text.Length} символов]";
}
