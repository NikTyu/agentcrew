using AgentCrew.Core.Tools;
using Microsoft.Extensions.AI;

namespace AgentCrew.Tests;

internal static class TestData
{
    public static SalesData Sales() => new(
    [
        new("2026-Q2", "Москва", "Умные часы", "Носимые", 100, 1_000_000m, 3),
        new("2026-Q2", "Казань", "Умные часы", "Носимые", 50, 500_000m, 2),
        new("2026-Q2", "Москва", "Ноутбуки", "Компьютеры", 20, 2_000_000m, 1),
        new("2026-Q3", "Москва", "Умные часы", "Носимые", 150, 1_500_000m, 4),
        new("2026-Q3", "Казань", "Умные часы", "Носимые", 60, 600_000m, 2),
        new("2026-Q3", "Москва", "Ноутбуки", "Компьютеры", 18, 1_800_000m, 3),
    ]);

    public static KnowledgeBase Knowledge() => new(
    [
        new("logistics", "Логистика", """
            # Логистика
            ## Инцидент в Новосибирске
            В августе на складе в Новосибирске произошёл сбой WMS, сроки доставки выросли до 7 дней.
            ## Возвраты
            Основные причины возвратов: брак и ошибки сборки.
            """),
        new("market", "Обзор рынка", """
            # Обзор рынка
            ## Тренды
            Спрос на умные часы растёт благодаря мониторингу здоровья.
            ## Ноутбуки
            Продажи ноутбуков падают из-за дефицита компонентов.
            """),
    ]);

    /// <summary>Фейковая модель: отдаёт заранее заданные ответы по очереди и запоминает запросы.</summary>
    public sealed class ScriptedChatClient(params ChatMessage[] replies) : IChatClient
    {
        private readonly Queue<ChatMessage> _replies = new(replies);

        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            var reply = _replies.Count > 0 ? _replies.Dequeue() : new ChatMessage(ChatRole.Assistant, "готово");
            return Task.FromResult(new ChatResponse(reply) { Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 } });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    public static ChatMessage ToolCall(string id, string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, name, args)]);
}
