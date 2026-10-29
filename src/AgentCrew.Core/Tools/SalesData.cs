using System.Globalization;
using System.Text;

namespace AgentCrew.Core.Tools;

public sealed record SalesRecord(string Quarter, string Region, string Product, string Category, int Units, decimal Revenue, int Returns);

public sealed record SalesAggregate(string Key, int Units, decimal Revenue, int Returns)
{
    public decimal ReturnRate => Units == 0 ? 0 : Math.Round(100m * Returns / Units, 1);
}

public sealed record GrowthRow(string Key, decimal RevenueFrom, decimal RevenueTo)
{
    public decimal GrowthPercent => RevenueFrom == 0 ? 0 : Math.Round(100m * (RevenueTo - RevenueFrom) / RevenueFrom, 1);
}

/// <summary>Табличные данные продаж (CSV) и агрегаты над ними — «источник истины» для агента-аналитика.</summary>
public sealed class SalesData(IReadOnlyList<SalesRecord> records)
{
    public static readonly string[] Dimensions = ["product", "region", "quarter", "category"];

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public IReadOnlyList<SalesRecord> Records { get; } = records;

    public IReadOnlyList<string> Quarters => [.. Records.Select(r => r.Quarter).Distinct().Order(StringComparer.Ordinal)];

    public static SalesData LoadFromCsv(string path)
    {
        if (!File.Exists(path))
        {
            return new SalesData([]);
        }

        var records = File.ReadLines(path)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(','))
            .Select(p => new SalesRecord(
                p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(),
                int.Parse(p[4], CultureInfo.InvariantCulture),
                decimal.Parse(p[5], CultureInfo.InvariantCulture),
                int.Parse(p[6], CultureInfo.InvariantCulture)))
            .ToList();
        return new SalesData(records);
    }

    public IReadOnlyList<SalesAggregate> Aggregate(string groupBy, string? quarter = null, string? region = null, string? product = null)
    {
        var key = KeySelector(groupBy);
        return Filter(quarter, region, product)
            .GroupBy(key)
            .Select(g => new SalesAggregate(g.Key, g.Sum(r => r.Units), g.Sum(r => r.Revenue), g.Sum(r => r.Returns)))
            .OrderByDescending(a => a.Revenue)
            .ToList();
    }

    public IReadOnlyList<GrowthRow> CompareQuarters(string? from = null, string? to = null, string groupBy = "product", string? region = null)
    {
        var quarters = Quarters;
        if (quarters.Count < 2 && (from is null || to is null))
        {
            return [];
        }
        from ??= quarters[^2];
        to ??= quarters[^1];
        RequireQuarter(from);
        RequireQuarter(to);

        var before = Aggregate(groupBy, from, region).ToDictionary(a => a.Key, a => a.Revenue);
        var after = Aggregate(groupBy, to, region).ToDictionary(a => a.Key, a => a.Revenue);
        return before.Keys.Union(after.Keys)
            .Select(k => new GrowthRow(k, before.GetValueOrDefault(k), after.GetValueOrDefault(k)))
            .OrderByDescending(r => r.GrowthPercent)
            .ToList();
    }

    public string DescribeSchema() =>
        $"Кварталы: {string.Join(", ", Quarters)}. " +
        $"Регионы: {string.Join(", ", Records.Select(r => r.Region).Distinct())}. " +
        $"Товары: {string.Join(", ", Records.Select(r => r.Product).Distinct())}. " +
        $"Категории: {string.Join(", ", Records.Select(r => r.Category).Distinct())}.";

    public static string FormatAggregates(IReadOnlyList<SalesAggregate> rows, string groupBy)
    {
        if (rows.Count == 0)
        {
            return "Нет данных по заданным фильтрам.";
        }
        var total = rows.Sum(r => r.Revenue);
        var sb = new StringBuilder()
            .AppendLine($"| {groupBy} | Выручка, ₽ | Доля | Продано, шт | Возвраты, % |")
            .AppendLine("|---|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            var share = total == 0 ? 0 : Math.Round(100m * r.Revenue / total, 1);
            sb.AppendLine($"| {r.Key} | {Money(r.Revenue)} | {Percent(share)} | {r.Units.ToString("N0", Ru)} | {Percent(r.ReturnRate)} |");
        }
        sb.Append($"| **Итого** | **{Money(total)}** | 100% | {rows.Sum(r => r.Units).ToString("N0", Ru)} | |");
        return sb.ToString();
    }

    public static string FormatGrowth(IReadOnlyList<GrowthRow> rows, string from, string to, string groupBy)
    {
        if (rows.Count == 0)
        {
            return "Недостаточно данных для сравнения кварталов.";
        }
        var sb = new StringBuilder()
            .AppendLine($"| {groupBy} | {from}, ₽ | {to}, ₽ | Рост |")
            .AppendLine("|---|---:|---:|---:|");
        foreach (var r in rows)
        {
            var sign = r.GrowthPercent > 0 ? "+" : "";
            sb.AppendLine($"| {r.Key} | {Money(r.RevenueFrom)} | {Money(r.RevenueTo)} | {sign}{Percent(r.GrowthPercent)} |");
        }
        return sb.ToString().TrimEnd();
    }

    private IEnumerable<SalesRecord> Filter(string? quarter, string? region, string? product)
    {
        IEnumerable<SalesRecord> q = Records;
        if (!string.IsNullOrWhiteSpace(quarter))
        {
            RequireQuarter(quarter);
            q = q.Where(r => r.Quarter.Equals(quarter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            q = q.Where(r => r.Region.Contains(region, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(product))
        {
            q = q.Where(r => r.Product.Contains(product, StringComparison.OrdinalIgnoreCase));
        }
        return q;
    }

    private void RequireQuarter(string quarter)
    {
        if (!Quarters.Contains(quarter, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Неизвестный квартал '{quarter}'. Доступны: {string.Join(", ", Quarters)}.");
        }
    }

    private static Func<SalesRecord, string> KeySelector(string groupBy) => groupBy.Trim().ToLowerInvariant() switch
    {
        "product" => r => r.Product,
        "region" => r => r.Region,
        "quarter" => r => r.Quarter,
        "category" => r => r.Category,
        _ => throw new ArgumentException($"Нельзя сгруппировать по '{groupBy}'. Допустимо: {string.Join(", ", Dimensions)}."),
    };

    private static string Money(decimal value) => value.ToString("N0", Ru);

    private static string Percent(decimal value) => value.ToString("0.0", Ru) + "%";
}
