namespace AgentCrew.Core.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Auto — OpenAI-совместимый провайдер, если задан ApiKey, иначе демо-режим;
    /// OpenAI — любой OpenAI-совместимый API (OpenAI, Ollama, LM Studio, vLLM, OpenRouter и т.д.);
    /// Demo — офлайн-сценарий без LLM для показа и тестов.
    /// </summary>
    public string Provider { get; set; } = "Auto";

    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Температура; пустое значение — не передавать (для reasoning-моделей).</summary>
    public float? Temperature { get; set; } = 0.2f;

    /// <summary>
    /// Уровень рассуждений для reasoning-моделей (low/medium/high; "none" отключает режим размышлений,
    /// например у qwen3 в Ollama). Пусто — не передавать.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    public int MaxRetries { get; set; } = 3;

    /// <summary>Таймаут одного запроса к API; локальным моделям нужен запас.</summary>
    public int NetworkTimeoutSeconds { get; set; } = 120;

    /// <summary>Задержка ответов демо-клиента, чтобы в UI было видно ход работы.</summary>
    public int DemoDelayMs { get; set; } = 400;

    public bool IsDemo =>
        Provider.Equals("Demo", StringComparison.OrdinalIgnoreCase)
        || (Provider.Equals("Auto", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ApiKey));
}
