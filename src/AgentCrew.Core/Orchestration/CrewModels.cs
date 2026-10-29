using AgentCrew.Core.Agents;

namespace AgentCrew.Core.Orchestration;

public sealed record ChatTurn(string Role, string Content);

public sealed record CrewRequest(string Task, IReadOnlyList<ChatTurn>? History = null);

public sealed record PlanStep(string Id, string Agent, string Task, IReadOnlyList<string> DependsOn);

public sealed record Plan(string Goal, IReadOnlyList<PlanStep> Steps);

public sealed record StepResult(PlanStep Step, string Output, bool Succeeded, TimeSpan Duration);

public sealed record CriticVerdict(bool Approved, int Score, IReadOnlyList<string> Issues, string Feedback);

public sealed record CrewResult(
    string Answer,
    Plan Plan,
    IReadOnlyList<StepResult> Steps,
    IReadOnlyList<CriticVerdict> Reviews,
    UsageSummary Usage,
    TimeSpan Duration);

public sealed record CrewOptions
{
    public int MaxPlanSteps { get; init; } = 5;

    /// <summary>Сколько раз критик может вернуть черновик на доработку.</summary>
    public int MaxRevisions { get; init; } = 2;

    /// <summary>Сколько последних реплик диалога передавать агентам как контекст.</summary>
    public int HistoryTurns { get; init; } = 6;

    public AgentRuntimeOptions Runtime { get; init; } = new();
}
