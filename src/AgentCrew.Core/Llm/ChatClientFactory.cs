using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace AgentCrew.Core.Llm;

/// <summary>
/// Создаёт IChatClient по конфигурации. Вся система работает через абстракцию
/// Microsoft.Extensions.AI, поэтому провайдер меняется одной строкой в appsettings.json.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(LlmOptions options)
    {
        if (options.IsDemo)
        {
            return new DemoChatClient(TimeSpan.FromMilliseconds(options.DemoDelayMs));
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "Для провайдера OpenAI нужен Llm:ApiKey (для локальной Ollama подойдёт любое значение, например 'ollama').");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.Endpoint),
                NetworkTimeout = TimeSpan.FromSeconds(options.NetworkTimeoutSeconds),
            });

        var builder = new RetryingChatClient(client.GetChatClient(options.Model).AsIChatClient(), options.MaxRetries).AsBuilder();
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            // Специфичный для провайдера параметр передаём через «сырые» опции OpenAI SDK
            // (API помечено как экспериментальное, отсюда OPENAI001).
#pragma warning disable OPENAI001
            var effort = new ChatReasoningEffortLevel(options.ReasoningEffort);
            builder.ConfigureOptions(o => o.RawRepresentationFactory ??= _ => new ChatCompletionOptions { ReasoningEffortLevel = effort });
#pragma warning restore OPENAI001
        }
        return builder.Build();
    }

    public static string Describe(LlmOptions options) =>
        options.IsDemo ? "Демо-режим (офлайн, без LLM)" : $"{options.Model} @ {new Uri(options.Endpoint).Host}";
}
