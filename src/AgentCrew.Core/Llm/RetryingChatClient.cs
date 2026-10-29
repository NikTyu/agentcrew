using System.ClientModel;
using Microsoft.Extensions.AI;

namespace AgentCrew.Core.Llm;

/// <summary>
/// Middleware в конвейере IChatClient: повторяет запрос с экспоненциальной задержкой
/// при временных ошибках провайдера (429, 5xx, сетевые сбои).
/// </summary>
public sealed class RetryingChatClient(IChatClient innerClient, int maxRetries = 3, TimeSpan? baseDelay = null)
    : DelegatingChatClient(innerClient)
{
    private readonly TimeSpan _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(history, options, cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_baseDelay * Math.Pow(2, attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status is 0 or 408 or 429 or >= 500,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false,
    };
}
