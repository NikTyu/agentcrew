using System.Text.Json;

namespace AgentCrew.Core.Orchestration;

/// <summary>
/// Достаёт JSON-объект из ответа модели. Модели часто оборачивают JSON в ```json ... ```
/// или добавляют пояснения — извлекаем первый сбалансированный объект.
/// </summary>
public static class JsonExtractor
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static bool TryParse<T>(string? text, out T? value) where T : class
    {
        value = null;
        var json = ExtractObject(text);
        if (json is null)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        while (start >= 0)
        {
            var end = FindMatchingBrace(text, start);
            if (end > start)
            {
                return text[start..(end + 1)];
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                    {
                        return i;
                    }
                    break;
            }
        }
        return -1;
    }
}
