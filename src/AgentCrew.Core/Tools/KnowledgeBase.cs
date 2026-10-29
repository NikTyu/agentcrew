using System.Text;

namespace AgentCrew.Core.Tools;

public sealed record KnowledgeDocument(string Id, string Title, string Content);

public sealed record KnowledgeChunk(string DocumentId, string DocumentTitle, string Section, string Text);

public sealed record SearchHit(KnowledgeChunk Chunk, double Score);

/// <summary>
/// Локальная база знаний из Markdown-документов с полнотекстовым поиском BM25.
/// Документы режутся на фрагменты по заголовкам — агент получает релевантные разделы, а не файлы целиком.
/// </summary>
public sealed class KnowledgeBase
{
    private const double K1 = 1.5;
    private const double B = 0.75;
    private const int MaxChunkChars = 1200;

    private readonly List<IndexedChunk> _chunks = [];
    private readonly Dictionary<string, int> _documentFrequency = [];
    private readonly double _averageLength;

    public KnowledgeBase(IEnumerable<KnowledgeDocument> documents)
    {
        Documents = [.. documents];
        foreach (var doc in Documents)
        {
            foreach (var chunk in Chunk(doc))
            {
                // Заголовки документа и раздела участвуют в поиске наравне с текстом.
                var tokens = TextTokenizer.Tokenize($"{chunk.DocumentTitle} {chunk.Section} {chunk.Text}");
                var tf = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
                _chunks.Add(new IndexedChunk(chunk, tf, tokens.Count));
                foreach (var term in tf.Keys)
                {
                    _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
                }
            }
        }
        _averageLength = _chunks.Count == 0 ? 1 : _chunks.Average(c => c.Length);
    }

    public IReadOnlyList<KnowledgeDocument> Documents { get; }

    public int ChunkCount => _chunks.Count;

    public static KnowledgeBase LoadFromDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return new KnowledgeBase([]);
        }

        var docs = Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file =>
            {
                var content = File.ReadAllText(file);
                var id = Path.GetFileNameWithoutExtension(file);
                var title = content.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? id;
                return new KnowledgeDocument(id, title, content);
            });
        return new KnowledgeBase(docs);
    }

    public IReadOnlyList<SearchHit> Search(string query, int topK = 4)
    {
        var terms = TextTokenizer.Tokenize(query).Distinct().ToList();
        if (terms.Count == 0 || _chunks.Count == 0)
        {
            return [];
        }

        return _chunks
            .Select(c => new SearchHit(c.Chunk, Score(c, terms)))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(Math.Clamp(topK, 1, 10))
            .ToList();
    }

    public KnowledgeDocument? FindDocument(string idOrTitle) =>
        Documents.FirstOrDefault(d => d.Id.Equals(idOrTitle, StringComparison.OrdinalIgnoreCase))
        ?? Documents.FirstOrDefault(d => d.Title.Contains(idOrTitle, StringComparison.OrdinalIgnoreCase));

    private double Score(IndexedChunk chunk, List<string> terms)
    {
        double score = 0;
        foreach (var term in terms)
        {
            if (!chunk.TermFrequency.TryGetValue(term, out var tf))
            {
                continue;
            }
            var df = _documentFrequency[term];
            var idf = Math.Log(1 + (_chunks.Count - df + 0.5) / (df + 0.5));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * chunk.Length / _averageLength));
        }
        return score;
    }

    private static IEnumerable<KnowledgeChunk> Chunk(KnowledgeDocument doc)
    {
        var section = doc.Title;
        var buffer = new StringBuilder();

        foreach (var rawLine in doc.Content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# "))
            {
                continue;
            }
            if (line.StartsWith("## ") || line.StartsWith("### "))
            {
                if (buffer.Length > 0)
                {
                    yield return new KnowledgeChunk(doc.Id, doc.Title, section, buffer.ToString().Trim());
                    buffer.Clear();
                }
                section = line.TrimStart('#', ' ');
                continue;
            }

            // Слишком длинные разделы режем по абзацам.
            if (buffer.Length > MaxChunkChars && string.IsNullOrWhiteSpace(line))
            {
                yield return new KnowledgeChunk(doc.Id, doc.Title, section, buffer.ToString().Trim());
                buffer.Clear();
                continue;
            }
            buffer.AppendLine(line);
        }

        if (buffer.ToString().Trim().Length > 0)
        {
            yield return new KnowledgeChunk(doc.Id, doc.Title, section, buffer.ToString().Trim());
        }
    }

    private sealed record IndexedChunk(KnowledgeChunk Chunk, Dictionary<string, int> TermFrequency, int Length);
}
