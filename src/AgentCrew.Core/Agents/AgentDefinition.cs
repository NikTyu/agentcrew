using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Agents;

/// <summary>Декларативное описание агента: роль, инструкции и набор инструментов.</summary>
public sealed record AgentDefinition
{
    /// <summary>Машинное имя (используется планировщиком в JSON-плане).</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Краткое описание возможностей — его видит планировщик при распределении задач.</summary>
    public required string Description { get; init; }

    /// <summary>Системный промпт агента.</summary>
    public required string Instructions { get; init; }

    public IReadOnlyList<AIFunction> Tools { get; init; } = [];

    /// <summary>Максимум раундов «модель → инструменты» до принудительного финального ответа.</summary>
    public int MaxIterations { get; init; } = 6;

    /// <summary>Просить у модели строго JSON (для планировщика и критика).</summary>
    public bool JsonOutput { get; init; }

    /// <summary>
    /// Проверять обоснованность ответа: агент вызвал хотя бы один инструмент, а числа и ссылки на источники
    /// взяты из результатов инструментов или входных данных. Если нет — агент получает замечание
    /// и продолжает работу (вызывает инструменты или исправляет ответ).
    /// </summary>
    public bool VerifyGrounding { get; init; }
}

/// <summary>Общие ограничения рантайма агентов (guardrails).</summary>
public sealed record AgentRuntimeOptions
{
    /// <summary>Температура генерации; null — не передавать (некоторые reasoning-модели её не принимают).</summary>
    public float? Temperature { get; init; } = 0.2f;

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Длинные результаты инструментов обрезаются, чтобы не раздувать контекст.</summary>
    public int MaxToolResultChars { get; init; } = 6000;

    /// <summary>Сколько раз агент может быть возвращён к работе из-за необоснованного ответа.</summary>
    public int MaxGroundingRetries { get; init; } = 2;

    /// <summary>Использовать JSON-режим провайдера для агентов с <see cref="AgentDefinition.JsonOutput"/>.</summary>
    public bool UseJsonMode { get; init; } = true;
}
