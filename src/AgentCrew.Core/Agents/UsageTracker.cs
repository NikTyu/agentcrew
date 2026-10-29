using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Agents;

public sealed record AgentUsage(string Agent, int LlmCalls, long InputTokens, long OutputTokens, int ToolCalls);

public sealed record UsageSummary(IReadOnlyList<AgentUsage> PerAgent)
{
    public int LlmCalls => PerAgent.Sum(a => a.LlmCalls);
    public long InputTokens => PerAgent.Sum(a => a.InputTokens);
    public long OutputTokens => PerAgent.Sum(a => a.OutputTokens);
    public int ToolCalls => PerAgent.Sum(a => a.ToolCalls);
}

/// <summary>Потокобезопасный учёт обращений к LLM, токенов и вызовов инструментов по агентам.</summary>
public sealed class UsageTracker
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public void AddLlmCall(string agent, UsageDetails? usage)
    {
        var c = _counters.GetOrAdd(agent, _ => new Counter());
        Interlocked.Increment(ref c.LlmCalls);
        Interlocked.Add(ref c.InputTokens, usage?.InputTokenCount ?? 0);
        Interlocked.Add(ref c.OutputTokens, usage?.OutputTokenCount ?? 0);
    }

    public void AddToolCall(string agent) =>
        Interlocked.Increment(ref _counters.GetOrAdd(agent, _ => new Counter()).ToolCalls);

    public UsageSummary Snapshot() => new(
        [.. _counters
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AgentUsage(kv.Key, kv.Value.LlmCalls, kv.Value.InputTokens, kv.Value.OutputTokens, kv.Value.ToolCalls))]);

    private sealed class Counter
    {
        public int LlmCalls;
        public long InputTokens;
        public long OutputTokens;
        public int ToolCalls;
    }
}
