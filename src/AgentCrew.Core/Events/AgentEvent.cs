namespace AgentCrew.Core.Events;

/// <summary>Тип события в трассе выполнения мультиагентной системы.</summary>
public enum AgentEventType
{
    RunStarted,
    PlanCreated,
    StepStarted,
    StepCompleted,
    AgentThinking,
    ToolCall,
    ToolResult,
    AgentMessage,
    CriticReview,
    FinalAnswer,
    Error,
    RunCompleted,
}

/// <summary>
/// Событие трассы. Всё, что делают агенты, публикуется как поток таких событий —
/// UI показывает их в реальном времени, тесты по ним проверяют поведение.
/// </summary>
public sealed record AgentEvent(AgentEventType Type, string Agent, string? Text = null, object? Data = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Идентификатор шага плана, к которому относится событие (если есть).</summary>
    public string? StepId { get; init; }
}
