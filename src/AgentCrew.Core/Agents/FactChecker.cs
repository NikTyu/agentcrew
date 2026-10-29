using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentCrew.Core.Agents;

/// <summary>
/// Детерминированная проверка обоснованности: значимые числа в тексте (проценты и числа от 1000)
/// и ссылки вида [документ › раздел] должны встречаться в источниках — результатах инструментов или входных данных.
/// Модели (особенно небольшие) выдумывают цифры, а LLM-критики склонны одобрять всё подряд —
/// эта проверка от модели не зависит.
/// </summary>
public static partial class FactChecker
{
    public static IReadOnlyList<string> FindUnsupportedNumbers(string draft, string sources)
    {
        var known = Numbers(sources).Select(n => n.Value).ToList();
        return Numbers(draft)
            .Where(n => n.IsPercent || (Math.Abs(n.Value) >= 1000 && !IsYear(n.Value)))
            .Where(n => !known.Any(k => Math.Abs(Math.Abs(k) - Math.Abs(n.Value)) <= Tolerance(n)))
            .Select(n => n.Text)
            .Distinct()
            .ToList();
    }

    /// <summary>Ссылки [документ › раздел], документа которых нет среди источников.</summary>
    public static IReadOnlyList<string> FindUnsupportedCitations(string draft, string sources) =>
        CitationRegex().Matches(draft)
            .Where(m => !sources.Contains($"[{m.Groups["doc"].Value.Trim()} ›", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Value)
            .Distinct()
            .ToList();

    private static bool IsYear(double value) => value is >= 1900 and <= 2100 && value == Math.Floor(value);

    // Проценты допускаем с округлением до целого, крупные числа — с точностью 0,5%.
    private static double Tolerance((string Text, double Value, bool IsPercent) n) =>
        n.IsPercent ? 0.5 : Math.Abs(n.Value) * 0.005;

    private static IEnumerable<(string Text, double Value, bool IsPercent)> Numbers(string text)
    {
        foreach (Match m in NumberRegex().Matches(text))
        {
            var digits = Regex.Replace(m.Groups["num"].Value, @"[\s  ]", "").Replace(',', '.');
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return (m.Value.Trim(), value, m.Groups["pct"].Success);
            }
        }
    }

    [GeneratedRegex(@"\[(?<doc>[^\[\]›]+)›[^\[\]]*\]")]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"(?<num>\d{1,3}(?:[   ]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?)(?<pct>\s?%)?")]
    private static partial Regex NumberRegex();
}
