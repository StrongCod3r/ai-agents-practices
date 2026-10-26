using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCalculator.Application.Configuration;
using SmartCalculator.Application.Abstractions;
using SmartCalculator.Infrastructure.Configuration;
using SmartCalculator.Infrastructure.Services;

namespace SmartCalculator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<AgentOptions>().BindConfiguration(AgentOptions.Section)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddTransient<OpenRouterReasoningHandler>();
        services.AddHttpClient("OpenRouter", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("X-OpenRouter-Title", "Smart Calculator Agent");
        }).AddHttpMessageHandler<OpenRouterReasoningHandler>();
        services.AddSingleton<IChatCompletionService>(services =>
        {
            var config = services.GetRequiredService<IConfiguration>();
            var key = OpenRouterConfiguration.GetApiKey(config);
            var client = new OpenAIClient(new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "not-configured" : key),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://openrouter.ai/api/v1"),
                    Transport = new HttpClientPipelineTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")),
                    RetryPolicy = new ClientRetryPolicy(0)
                });
            return new OpenAIChatCompletionService(services.GetRequiredService<IOptions<AgentOptions>>().Value.Model, client);
        });

        services.AddTransient<IAgentSessionFactory, SemanticKernelSessionFactory>();
        services.AddSingleton<IProviderAvailability, ProviderAvailability>();
        services.AddSingleton<IProviderFailureClassifier, ProviderFailureClassifier>();
        return services;
    }
}
