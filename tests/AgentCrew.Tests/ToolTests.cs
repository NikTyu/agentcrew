using AgentCrew.Core.Tools;

namespace AgentCrew.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("2 + 2 * 2", 6)]
    [InlineData("(2 + 2) * 2", 8)]
    [InlineData("-3 + 5", 2)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("round(2.345, 2)", 2.35)]
    [InlineData("pct(25, 200)", 12.5)]
    [InlineData("max(1, 7, 3) - min(4, 2)", 5)]
    [InlineData("1_500_000 - 1_000_000", 500000)]
    public void Evaluates_expressions(string expression, double expected) =>
        Assert.Equal(expected, Calculator.Evaluate(expression), 6);

    [Theory]
    [InlineData("")]
    [InlineData("2 +")]
    [InlineData("(1 + 2")]
    [InlineData("foo(1)")]
    [InlineData("2 $ 3")]
    public void Rejects_invalid_input(string expression) =>
        Assert.ThrowsAny<Exception>(() => Calculator.Evaluate(expression));

    [Fact]
    public void Division_by_zero_is_an_error() =>
        Assert.Throws<DivideByZeroException>(() => Calculator.Evaluate("1 / (2 - 2)"));
}

public class KnowledgeBaseTests
{
    [Theory]
    [InlineData("продажи", "продаж")]
    [InlineData("продажам", "продаж")]
    [InlineData("роста", "рост")]
    [InlineData("Ёлка", "елк")]
    public void Stemmer_normalizes_word_forms(string word, string expected) =>
        Assert.Equal(expected, TextTokenizer.Tokenize(word).Single());

    [Fact]
    public void Search_finds_relevant_section_across_word_forms()
    {
        var kb = TestData.Knowledge();

        var hits = kb.Search("почему задержки доставки в Новосибирске?");

        Assert.NotEmpty(hits);
        Assert.Equal("logistics", hits[0].Chunk.DocumentId);
        Assert.Equal("Инцидент в Новосибирске", hits[0].Chunk.Section);
    }

    [Fact]
    public void Search_returns_nothing_for_unrelated_query() =>
        Assert.Empty(TestData.Knowledge().Search("квантовая гравитация"));

    [Fact]
    public void Documents_are_chunked_by_headings() =>
        Assert.Equal(4, TestData.Knowledge().ChunkCount);
}

public class SalesDataTests
{
    [Fact]
    public void Aggregates_by_product_with_filters()
    {
        var rows = TestData.Sales().Aggregate("product", quarter: "2026-Q3");

        Assert.Equal(["Умные часы", "Ноутбуки"], rows.Select(r => r.Key));
        Assert.Equal(2_100_000m, rows[0].Revenue);
        Assert.Equal(210, rows[0].Units);
    }

    [Fact]
    public void Compares_last_two_quarters_by_default()
    {
        var growth = TestData.Sales().CompareQuarters();

        var watches = growth.Single(g => g.Key == "Умные часы");
        Assert.Equal(40m, watches.GrowthPercent);
        Assert.Equal(-10m, growth.Single(g => g.Key == "Ноутбуки").GrowthPercent);
        Assert.Equal("Умные часы", growth[0].Key);
    }

    [Fact]
    public void Unknown_quarter_or_dimension_gives_helpful_error()
    {
        var sales = TestData.Sales();

        var q = Assert.Throws<ArgumentException>(() => sales.Aggregate("product", quarter: "2030-Q1"));
        Assert.Contains("2026-Q2", q.Message);
        Assert.Throws<ArgumentException>(() => sales.Aggregate("color"));
    }

    [Fact]
    public void Formats_markdown_table_with_totals()
    {
        var table = SalesData.FormatAggregates(TestData.Sales().Aggregate("region"), "region");

        Assert.StartsWith("| region |", table);
        Assert.Contains("**Итого**", table);
    }
}
