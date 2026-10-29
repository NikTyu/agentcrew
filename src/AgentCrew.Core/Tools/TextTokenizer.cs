using System.Text.RegularExpressions;

namespace AgentCrew.Core.Tools;

/// <summary>
/// Простой токенизатор для русского и английского текста: нижний регистр, ё→е,
/// удаление стоп-слов и облегчённый стемминг (отсечение типичных окончаний).
/// Для поиска по небольшой базе знаний этого достаточно и не нужны внешние зависимости.
/// </summary>
public static partial class TextTokenizer
{
    private static readonly HashSet<string> StopWords =
    [
        "и", "в", "во", "на", "с", "со", "по", "к", "ко", "о", "об", "от", "до", "из", "за", "для", "при", "без",
        "не", "ни", "но", "а", "или", "что", "как", "это", "этот", "эта", "эти", "то", "так", "же", "ли", "бы",
        "у", "мы", "вы", "он", "она", "они", "оно", "я", "ты", "наш", "наша", "наши", "ваш", "все", "всё", "его",
        "ее", "их", "там", "тут", "где", "когда", "какой", "какие", "какая", "каких", "который", "которые", "есть",
        "был", "была", "были", "быть", "будет", "чем", "мне", "нам", "мой", "сам", "свой", "также", "только",
        "the", "a", "an", "of", "to", "in", "on", "for", "and", "or", "is", "are", "with", "by",
    ];

    // Сортируем по убыванию длины, чтобы отсекать самое длинное совпадение.
    private static readonly string[] Endings = new[]
    {
        "иями", "ями", "ами", "ией", "ого", "его", "ому", "ему", "ыми", "ими", "ость", "ости",
        "ых", "их", "ая", "яя", "ое", "ее", "ые", "ие", "ой", "ей", "ий", "ый", "ом", "ем", "ам", "ям",
        "ах", "ях", "ов", "ев", "ию", "ью", "ия", "ья", "ть", "ет", "ют", "ут", "ит", "ат", "ят",
        "а", "я", "о", "е", "ы", "и", "у", "ю", "ь", "й",
    }.OrderByDescending(e => e.Length).ToArray();

    public static IReadOnlyList<string> Tokenize(string text)
    {
        var normalized = text.ToLowerInvariant().Replace('ё', 'е');
        var tokens = new List<string>();
        foreach (Match m in WordRegex().Matches(normalized))
        {
            var word = m.Value;
            if (StopWords.Contains(word))
            {
                continue;
            }
            tokens.Add(Stem(word));
        }
        return tokens;
    }

    public static string Stem(string word)
    {
        if (word.Length <= 3 || !IsCyrillic(word))
        {
            return word;
        }

        foreach (var ending in Endings)
        {
            if (word.EndsWith(ending, StringComparison.Ordinal) && word.Length - ending.Length >= 3)
            {
                return word[..^ending.Length];
            }
        }
        return word;
    }

    private static bool IsCyrillic(string word) => word.Any(c => c is >= 'а' and <= 'я');

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
