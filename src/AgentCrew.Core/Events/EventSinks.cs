using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AgentCrew.Core.Events;

/// <summary>Приёмник событий трассы. Реализации должны быть потокобезопасными: шаги плана идут параллельно.</summary>
public interface IAgentEventSink
{
    ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken = default);
}

public sealed class NullEventSink : IAgentEventSink
{
    public static readonly NullEventSink Instance = new();

    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Пишет события в канал — используется для стриминга в браузер через SSE.</summary>
public sealed class ChannelEventSink(ChannelWriter<AgentEvent> writer) : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken = default) =>
        writer.WriteAsync(evt, cancellationToken);
}

/// <summary>Собирает события в память — для тестов и консольных сценариев.</summary>
public sealed class CollectingEventSink : IAgentEventSink
{
    private readonly ConcurrentQueue<AgentEvent> _events = new();

    public IReadOnlyList<AgentEvent> Events => [.. _events];

    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(evt);
        return ValueTask.CompletedTask;
    }
}
