using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Infrastructure.Persistence;
using System.ClientModel;
using System.ClientModel.Primitives;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenAI;

namespace MemoryAssistant.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEMORY_LIVE_TESTS") != "1")
            Skip = "Set MEMORY_LIVE_TESTS=1, OPENROUTER_API_KEY and Agent__Model to opt into paid provider tests.";
    }
}

public sealed class LiveTests
{
    [LiveFact]
    public async Task Openrouter_can_call_a_tool()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var model = Environment.GetEnvironmentVariable("Agent__Model");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENROUTER_API_KEY is required.");
        Assert.False(string.IsNullOrWhiteSpace(model), "Agent__Model is required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var http = new HttpClient();
        var provider = new OpenAIClient(new ApiKeyCredential(key!), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1"), Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        });
        using var chat = provider.GetChatClient(model!).AsIChatClient();
        var steps = new List<ToolStep>();
        var answer = await new AgentRunner(chat, Options.Create(new AgentOptions { Model = model! }))
            .RunAsync([new(ChatRole.User, "Llama a ReadPreference para consultar mi preferencia y después responde con el valor obtenido.")],
                [AIFunctionFactory.Create(() => "C#", "ReadPreference")], steps, timeout.Token);
        Assert.NotEmpty(answer);
        Assert.Contains(steps, x => x.Tool == "ReadPreference");
    }

    [OllamaFact]
    public async Task Ollama_generates_local_embeddings_without_openrouter_key()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var options = new EmbeddingOptions();
        config.GetSection("Embeddings").Bind(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds + 5));
        using var http = new HttpClient();
        var vector = await new OllamaEmbeddingClient(http, Options.Create(options)).GenerateAsync("Prefiero C#", timeout.Token);
        Assert.Equal(options.Dimensions, vector.Length);
        Assert.All(vector, x => Assert.True(float.IsFinite(x)));
        Assert.Contains(vector, x => x != 0);
    }
}

public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEMORY_OLLAMA_TESTS") != "1")
            Skip = "Set MEMORY_OLLAMA_TESTS=1 with Ollama running and embeddinggemma-2 downloaded.";
    }
}
