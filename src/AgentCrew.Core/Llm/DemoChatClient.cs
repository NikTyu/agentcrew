using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCrew.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Llm;

/// <summary>
/// Офлайн-«модель» для демонстрации и тестов. Ведёт себя как LLM с function calling:
/// по роли агента (agent_id в системном промпте) возвращает план, вызовы инструментов или текст.
/// Инструменты при этом настоящие — данные и база знаний реальные, сценарий детерминированный.
/// </summary>
public sealed partial class DemoChatClient(TimeSpan? delay = null) : IChatClient
{
    private static readonly string[] DataKeywords =
        ["продаж", "выручк", "рост", "динамик", "цифр", "квартал", "товар", "регион", "показател", "доля", "отчёт", "отчет", "лидер"];

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] SmallTalk = ["привет", "здравств", "что умеешь", "кто ты", "помощь", "спасибо"];

    private readonly TimeSpan _delay = delay ?? TimeSpan.FromMilliseconds(400);
    private int _callCounter;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken);
        }

        var history = messages.ToList();
        var system = history.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        var input = history.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var toolResults = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        var canCallTools = options?.Tools is { Count: > 0 };

        var reply = AgentIdRegex().Match(system).Groups[1].Value switch
        {
            "planner" => Text(Plan(input)),
            "researcher" when canCallTools && toolResults.Count == 0 => Calls(("search_knowledge_base", new() { ["query"] = Section(input, CrewMarkers.Task), ["topK"] = 3 })),
            "researcher" => Text(SummarizeResearch(toolResults)),
            "analyst" when canCallTools && toolResults.Count == 0 => Calls(
                ("compare_quarters", new() { ["groupBy"] = "product" }),
                ("compare_quarters", new() { ["groupBy"] = "region" })),
            "analyst" when canCallTools && toolResults.Count == 2 && TopGrowth(Result(toolResults[0])) is { } top => Calls(
                ("calculator", new() { ["expression"] = $"{top.To} - {top.From}" })),
            "analyst" => Text(SummarizeAnalysis(toolResults)),
            "writer" => Text(Write(input)),
            "critic" => Text(Review(input)),
            _ => Text("Демо-режим: неизвестная роль агента."),
        };

        var inputChars = history.Sum(m => m.Text.Length);
        return new ChatResponse(reply)
        {
            ModelId = "demo",
            Usage = new UsageDetails
            {
                InputTokenCount = inputChars / 4,
                OutputTokenCount = Math.Max(1, reply.Text.Length / 4 + reply.Contents.OfType<FunctionCallContent>().Count() * 20),
            },
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("demo", null, "demo-scripted")
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    // ---------- Роли ----------

    private static string Plan(string input)
    {
        var request = Section(input, CrewMarkers.Request);
        var lower = request.ToLowerInvariant();

        if (request.Length < 40 && SmallTalk.Any(lower.Contains))
        {
            return Json(new { goal = "Поприветствовать пользователя и рассказать о возможностях", steps = Array.Empty<object>() });
        }

        var steps = new List<object>();
        var needsData = DataKeywords.Any(lower.Contains);
        if (needsData)
        {
            steps.Add(new { id = "s1", agent = "analyst", task = "Посчитай динамику выручки между двумя последними кварталами по товарам и регионам", dependsOn = Array.Empty<string>() });
        }
        steps.Add(new { id = $"s{steps.Count + 1}", agent = "researcher", task = $"Найди в базе знаний контекст и причины: {request}", dependsOn = Array.Empty<string>() });
        if (needsData && lower.Contains("рекоменд"))
        {
            steps.Add(new { id = $"s{steps.Count + 1}", agent = "researcher", task = "Найди риски, планы маркетинга и проблемы логистики, которые влияют на лидеров и аутсайдеров продаж", dependsOn = new[] { "s1" } });
        }

        return Json(new { goal = $"Подготовить обоснованный ответ: {request}", steps });
    }

    private static string SummarizeResearch(List<FunctionResultContent> results)
    {
        var sb = new StringBuilder("Найденные факты:\n");
        var found = false;
        foreach (Match hit in HitRegex().Matches(string.Join("\n\n", results.Select(Result))))
        {
            var sentences = SentenceRegex().Matches(hit.Groups["text"].Value.Replace("\n", " "))
                .Select(m => m.Value.Trim().TrimStart('-', '*', ' '))
                .Where(s => s.Length > 20)
                .Take(2);
            foreach (var sentence in sentences)
            {
                sb.AppendLine($"- {sentence} [{hit.Groups["source"].Value}]");
                found = true;
            }
        }
        return found ? sb.ToString().TrimEnd() : "В базе знаний не нашлось информации по этому вопросу.";
    }

    private static string SummarizeAnalysis(List<FunctionResultContent> results)
    {
        var byProduct = results.Count > 0 ? Result(results[0]) : "";
        var byRegion = results.Count > 1 ? Result(results[1]) : "";
        var delta = results.Count > 2 ? Result(results[2]) : null;
        var rows = GrowthRows(byProduct);

        var sb = new StringBuilder()
            .AppendLine("**Динамика выручки по товарам:**").AppendLine(byProduct).AppendLine()
            .AppendLine("**Динамика выручки по регионам:**").AppendLine(byRegion).AppendLine();
        if (rows.Count > 0)
        {
            var (top, worst) = (rows[0], rows[^1]);
            sb.Append($"Лидер роста — «{top.Name}» ({top.Growth})");
            if (delta is not null && double.TryParse(delta, System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                sb.Append($", абсолютный прирост {d:N0} ₽");
            }
            sb.AppendLine($". Слабее всего — «{worst.Name}» ({worst.Growth}).");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Write(string input)
    {
        var request = Section(input, CrewMarkers.Request);
        var results = Between(input, CrewMarkers.Results, CrewMarkers.PreviousDraft);
        if (!results.Contains("### "))
        {
            return "Здравствуйте! Я ИИ-ассистент команды аналитиков «ТехноЛавки». Могу:\n\n" +
                   "- разобрать продажи по товарам, регионам и кварталам;\n" +
                   "- найти факты во внутренних документах — стратегии, политиках, отчётах о рынке;\n" +
                   "- подготовить сводку с выводами и рекомендациями.\n\n" +
                   "Например: *«Какие товары выросли в 3 квартале и что нам делать дальше?»*";
        }

        var sb = new StringBuilder($"## Ответ на запрос\n\n> {request}\n\n");
        foreach (var section in results.Split("### ", StringSplitOptions.RemoveEmptyEntries).Where(s => s.StartsWith('[')))
        {
            var header = section[..section.IndexOf('\n')];
            var body = section[(section.IndexOf('\n') + 1)..].Trim();
            var title = header.Contains("Аналитик") ? "Цифры и динамика" : "Контекст из базы знаний";
            sb.AppendLine($"### {title}").AppendLine(body).AppendLine();
        }

        if (input.Contains(CrewMarkers.Feedback))
        {
            // Первая таблица динамики — по товарам, вторая — по регионам.
            var tables = GrowthTables(results);
            var recommendations = new List<string>();
            if (tables.Count > 0)
            {
                var products = tables[0];
                recommendations.Add($"Нарастить закупки и маркетинг категории «{products[0].Name}» — лидера роста ({products[0].Growth}).");
                recommendations.Add($"Разобрать причины снижения «{products[^1].Name}» ({products[^1].Growth}) и пересмотреть ассортимент и цены.");
            }
            if (tables.Count > 1)
            {
                var regions = tables[1];
                recommendations.Add($"Восстановить продажи в регионе «{regions[^1].Name}» ({regions[^1].Growth}): проверить логистику и качество сборки заказов.");
            }
            recommendations.Add("Закрыть риски, отмеченные в документах, и ежемесячно отслеживать долю возвратов по регионам.");

            sb.AppendLine("## Рекомендации");
            for (var i = 0; i < recommendations.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {recommendations[i]}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Review(string input)
    {
        var draft = Between(input, CrewMarkers.Draft, null);
        return draft.Contains("## Рекомендации")
            ? Json(new { approved = true, score = 9, issues = Array.Empty<string>(), feedback = "Ответ полный, цифры совпадают с данными аналитика, есть практические выводы." })
            : Json(new
            {
                approved = false,
                score = 6,
                issues = new[] { "Нет раздела с практическими рекомендациями", "Выводы не связаны с действиями для бизнеса" },
                feedback = "Добавь раздел «Рекомендации» с 2–3 конкретными действиями, опирающимися на цифры и факты.",
            });
    }

    // ---------- Вспомогательное ----------

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private ChatMessage Calls(params (string Name, Dictionary<string, object?> Args)[] calls) =>
        new(ChatRole.Assistant, [.. calls.Select(c => (AIContent)new FunctionCallContent($"call_{Interlocked.Increment(ref _callCounter)}", c.Name, c.Args))]);

    private static string Result(FunctionResultContent r) => r.Result?.ToString() ?? "";

    private static string Section(string input, string marker)
    {
        var line = input.Split('\n').FirstOrDefault(l => l.StartsWith(marker, StringComparison.Ordinal));
        return line is null ? input.Trim() : line[marker.Length..].Trim();
    }

    private static string Between(string input, string start, string? end)
    {
        var i = input.IndexOf(start, StringComparison.Ordinal);
        if (i < 0)
        {
            return "";
        }
        i += start.Length;
        var j = end is null ? -1 : input.IndexOf(end, i, StringComparison.Ordinal);
        return (j < 0 ? input[i..] : input[i..j]).Trim();
    }

    private static List<(string Name, string Growth, long From, long To)> GrowthRows(string table) =>
        [.. GrowthRowRegex().Matches(table).Select(m => (
            m.Groups["name"].Value.Trim(),
            m.Groups["growth"].Value,
            long.Parse(Digits(m.Groups["from"].Value)),
            long.Parse(Digits(m.Groups["to"].Value))))];

    private static List<List<(string Name, string Growth, long From, long To)>> GrowthTables(string text) =>
        [.. text.Split("\n\n").Select(GrowthRows).Where(rows => rows.Count > 0)];

    private static (long From, long To)? TopGrowth(string table) =>
        GrowthRows(table) is { Count: > 0 } rows ? (rows[0].From, rows[0].To) : null;

    private static string Digits(string s) => new([.. s.Where(char.IsDigit)]);

    [GeneratedRegex(@"agent_id:\s*(\w+)")]
    private static partial Regex AgentIdRegex();

    [GeneratedRegex(@"\[(?<source>[^\]]+)\] \(релевантность [^)]+\)\n(?<text>(?:(?!\n\[).)+)", RegexOptions.Singleline)]
    private static partial Regex HitRegex();

    [GeneratedRegex(@"[^.!?]+[.!?]")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"^\| (?<name>[^|*]+?) \| (?<from>[\d\s]+) \| (?<to>[\d\s]+) \| (?<growth>[+-]?[\d.,]+%) \|", RegexOptions.Multiline)]
    private static partial Regex GrowthRowRegex();
}
