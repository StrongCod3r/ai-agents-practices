using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace MemoryAssistant.Infrastructure.AI;

public sealed class ConfiguredChatClient(IChatClient inner, IConfiguration configuration)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(OpenRouterConfiguration.ApiKey(configuration))
            || string.IsNullOrWhiteSpace(configuration["Agent:Model"])
        )
            throw new AssistantException(
                FailureKind.Unavailable,
                "provider_configuration",
                "Configura OPENROUTER_API_KEY y Agent:Model."
            );
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception exception)
            when (exception is System.ClientModel.ClientResultException or HttpRequestException)
        {
            throw new Diagnostics.FailureTranslator().Translate(exception)!;
        }
    }
}
