namespace AgentCrew.Core.Orchestration;

/// <summary>
/// Метки разделов во входных сообщениях агентов. Вынесены в константы, чтобы оркестратор
/// и демо-клиент (офлайн-режим без LLM) разбирали сообщения одинаково.
/// </summary>
public static class CrewMarkers
{
    public const string AgentId = "agent_id:";
    public const string Request = "Запрос пользователя:";
    public const string Goal = "Общая цель:";
    public const string Task = "Твоя задача:";
    public const string History = "История диалога:";
    public const string Dependencies = "Результаты шагов, от которых зависит задача:";
    public const string Results = "Результаты исполнителей:";
    public const string Draft = "Черновик ответа:";
    public const string PreviousDraft = "Предыдущий черновик:";
    public const string Feedback = "Замечания критика:";
}
