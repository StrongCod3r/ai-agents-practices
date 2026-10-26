using System.ClientModel;
using System.ClientModel.Primitives;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Infrastructure.Configuration;
using MemoryAssistant.Infrastructure.Diagnostics;
using MemoryAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Pgvector.EntityFrameworkCore;

namespace MemoryAssistant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<AgentOptions>()
            .BindConfiguration("Agent")
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services
            .AddOptions<EmbeddingOptions>()
            .BindConfiguration("Embeddings")
            .ValidateDataAnnotations()
            .Validate(
                options =>
                    Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                "Embeddings:BaseUrl debe ser una URL HTTP o HTTPS absoluta."
            )
            .ValidateOnStart();
        services.AddDbContext<MemoryDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Memory"),
                pg => pg.UseVector().UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            )
        );
        services.AddScoped<IMemoryRepository, EfMemoryRepository>();
        services.AddScoped<IConversationRepository, EfConversationRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDataStoreHealth, DataStoreHealth>();
        services.AddSingleton<IFailureTranslator, FailureTranslator>();
        services.AddHttpClient("OpenRouter", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan
        );
        services.AddSingleton<IChatClient>(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            var client = new OpenAIClient(
                new ApiKeyCredential(OpenRouterConfiguration.ApiKey(config) ?? "not-configured"),
                new()
                {
                    Endpoint = new Uri("https://openrouter.ai/api/v1"),
                    Transport = new HttpClientPipelineTransport(
                        provider.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")
                    ),
                    RetryPolicy = new ClientRetryPolicy(0),
                }
            );
            return new ConfiguredChatClient(
                client
                    .GetChatClient(
                        config["Agent:Model"] is { Length: > 0 } model ? model : "not-configured"
                    )
                    .AsIChatClient(),
                config
            );
        });
        services.AddScoped<IMemoryProcessor, StructuredMemoryProcessor>();
        services.AddScoped<IAgentRunner, AgentRunner>();
        return services;
    }

    public static async Task MigrateMemoryDatabaseAsync(
        this IServiceProvider services,
        CancellationToken ct = default
    )
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.MigrateAsync(ct);
    }
}
