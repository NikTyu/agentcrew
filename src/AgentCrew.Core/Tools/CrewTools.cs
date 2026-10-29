using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Tools;

/// <summary>
/// Инструменты агентов как <see cref="AIFunction"/>: AIFunctionFactory сам строит JSON-схему
/// параметров из сигнатуры и атрибутов [Description], которую видит модель.
/// </summary>
public static class CrewTools
{
    public static AIFunction SearchKnowledgeBase(KnowledgeBase kb) => AIFunctionFactory.Create(
        ([Description("Поисковый запрос: ключевые слова или вопрос на русском языке")] string query,
         [Description("Сколько фрагментов вернуть, от 1 до 8")] int topK = 4) =>
        {
            var hits = kb.Search(query, Math.Clamp(topK, 1, 8));
            if (hits.Count == 0)
            {
                return $"По запросу «{query}» ничего не найдено. Попробуй другие ключевые слова.";
            }

            var sb = new StringBuilder();
            foreach (var hit in hits)
            {
                sb.AppendLine($"[{hit.Chunk.DocumentId} › {hit.Chunk.Section}] (релевантность {hit.Score.ToString("0.00", CultureInfo.InvariantCulture)})")
                  .AppendLine(hit.Chunk.Text)
                  .AppendLine();
            }
            return sb.ToString().TrimEnd();
        },
        "search_knowledge_base",
        "Полнотекстовый поиск по внутренней базе знаний компании (политики, отчёты, планы). Возвращает релевантные фрагменты с указанием источника.");

    public static AIFunction ListDocuments(KnowledgeBase kb) => AIFunctionFactory.Create(
        () => string.Join("\n", kb.Documents.Select(d => $"- {d.Id}: {d.Title}")),
        "list_documents",
        "Список всех документов базы знаний (идентификатор и заголовок).");

    public static AIFunction ReadDocument(KnowledgeBase kb) => AIFunctionFactory.Create(
        ([Description("Идентификатор документа из list_documents или search_knowledge_base")] string documentId) =>
            kb.FindDocument(documentId)?.Content
            ?? $"Документ '{documentId}' не найден. Доступны: {string.Join(", ", kb.Documents.Select(d => d.Id))}.",
        "read_document",
        "Прочитать документ базы знаний целиком.");

    public static AIFunction SalesReport(SalesData sales) => AIFunctionFactory.Create(
        ([Description("Измерение группировки: product, region, quarter или category")] string groupBy,
         [Description("Фильтр по кварталу, например 2026-Q3 (необязательно)")] string? quarter = null,
         [Description("Фильтр по региону (необязательно)")] string? region = null,
         [Description("Фильтр по товару (необязательно)")] string? product = null) =>
            SalesData.FormatAggregates(sales.Aggregate(groupBy, quarter, region, product), groupBy),
        "sales_report",
        "Сводка продаж: выручка, доля, количество и процент возвратов с группировкой и фильтрами. " +
        "Используй для любых вопросов о цифрах продаж.");

    public static AIFunction CompareQuarters(SalesData sales) => AIFunctionFactory.Create(
        ([Description("Начальный квартал, например 2026-Q2. По умолчанию — предпоследний")] string? fromQuarter = null,
         [Description("Конечный квартал, например 2026-Q3. По умолчанию — последний")] string? toQuarter = null,
         [Description("Измерение: product, region или category")] string groupBy = "product",
         [Description("Фильтр по региону (необязательно)")] string? region = null) =>
        {
            var quarters = sales.Quarters;
            var from = fromQuarter ?? (quarters.Count >= 2 ? quarters[^2] : "?");
            var to = toQuarter ?? (quarters.Count >= 1 ? quarters[^1] : "?");
            return SalesData.FormatGrowth(sales.CompareQuarters(fromQuarter, toQuarter, groupBy, region), from, to, groupBy);
        },
        "compare_quarters",
        "Динамика выручки между двумя кварталами (рост/падение в %) по товарам, регионам или категориям.");

    public static AIFunction DataSchema(SalesData sales) => AIFunctionFactory.Create(
        sales.DescribeSchema,
        "describe_sales_data",
        "Какие кварталы, регионы, товары и категории есть в данных продаж.");

    public static AIFunction Calculate() => AIFunctionFactory.Create(
        ([Description("Арифметическое выражение: + - * / ^, скобки, round(x, n), sqrt, abs, min, max, pct(a, b). Десятичный разделитель — точка")] string expression) =>
            Calculator.Evaluate(expression).ToString("0.####", CultureInfo.InvariantCulture),
        "calculator",
        "Точный калькулятор. Используй для любых вычислений вместо подсчёта в уме.");
}
