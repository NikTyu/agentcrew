using AgentCrew.Core.Agents;

namespace AgentCrew.Core.Orchestration;

/// <summary>Состав команды: служебные роли (планировщик, редактор, критик) и исполнители.</summary>
public sealed class AgentCatalog
{
    public AgentCatalog(AgentDefinition planner, AgentDefinition writer, AgentDefinition critic, IEnumerable<AgentDefinition> workers)
    {
        Planner = planner;
        Writer = writer;
        Critic = critic;
        Workers = workers.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        if (Workers.Count == 0)
        {
            throw new ArgumentException("Нужен хотя бы один агент-исполнитель.", nameof(workers));
        }
    }

    public AgentDefinition Planner { get; }

    public AgentDefinition Writer { get; }

    public AgentDefinition Critic { get; }

    public IReadOnlyDictionary<string, AgentDefinition> Workers { get; }

    public IEnumerable<AgentDefinition> All => [Planner, .. Workers.Values, Writer, Critic];
}
