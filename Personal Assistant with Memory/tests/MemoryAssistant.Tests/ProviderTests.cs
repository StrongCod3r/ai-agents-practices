using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Infrastructure.Persistence;
using System.Net;
using System.Text;
using System.Text.Json;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;

namespace MemoryAssistant.Tests;

public sealed class ProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task Embedding_adapter_uses_real_json_contract_model_and_dimensions()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("http://localhost:11435/api/embed", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("fake-embedding", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("dimensions").GetInt32());
            Assert.Equal("Una preferencia", body.RootElement.GetProperty("input").GetString());
            Assert.False(body.RootElement.GetProperty("truncate").GetBoolean());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"embeddings\":[[1,0,0]]}", Encoding.UTF8, "application/json") };
        }));
        var adapter = new OllamaEmbeddingClient(http,
            Options.Create(new EmbeddingOptions { Model = "fake-embedding", Dimensions = 3 }));
        Assert.Equal(new float[] { 1, 0, 0 }, await adapter.GenerateAsync("Una preferencia", default));
    }

    [Theory]
    [InlineData("{\"embeddings\":[[1,0]]}")]
    [InlineData("{\"embeddings\":[[0,0,0]]}")]
    [InlineData("{\"embeddings\":[[1,0,0],[0,1,0]]}")]
    [InlineData("{\"embeddings\":[[1,\"NaN\",0]]}")]
    [InlineData("{\"embeddings\":[[1e100,0,0]]}")]
    [InlineData("{}")]
    [InlineData("{\"embeddings\":[]}")]
    [InlineData("not json")]
    public async Task Invalid_embedding_vectors_are_rejected(string json)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));
        var adapter = new OllamaEmbeddingClient(http,
            Options.Create(new EmbeddingOptions { Dimensions = 3 }));
        var error = await Assert.ThrowsAsync<AssistantException>(() => adapter.GenerateAsync("x", default));
        Assert.Equal("invalid_embedding", error.Code);
    }

    [Fact]
    public async Task Ollama_endpoint_is_configurable_without_a_provider_key()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("http://127.0.0.1:12000/api/embed", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"embeddings\":[[1,0,0]]}") });
        }));
        var adapter = new OllamaEmbeddingClient(http, Options.Create(new EmbeddingOptions
        { BaseUrl = "http://127.0.0.1:12000/", Dimensions = 3 }));
        Assert.Equal(3, (await adapter.GenerateAsync("preferencia", default)).Length);
    }

    [Fact]
    public async Task Ollama_http_errors_do_not_expose_provider_response_bodies()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        { Content = new StringContent("private provider detail") })));
        var error = await Assert.ThrowsAsync<AssistantException>(() =>
            new OllamaEmbeddingClient(http, Options.Create(new EmbeddingOptions())).GenerateAsync("x", default));
        Assert.Equal("embedding_provider", error.Code);
        Assert.Contains("404", error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task Ollama_connection_failure_is_translated()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private detail")));
        var error = await Assert.ThrowsAsync<AssistantException>(() =>
            new OllamaEmbeddingClient(http, Options.Create(new EmbeddingOptions())).GenerateAsync("x", default));
        Assert.Equal("embedding_connection", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ollama_distinguishes_timeout_from_caller_cancellation(bool callerCancels)
    {
        using var caller = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, token) =>
        {
            if (callerCancels) caller.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(callerCancels ? caller.Token : new CancellationToken(true));
        }));
        var adapter = new OllamaEmbeddingClient(http, Options.Create(new EmbeddingOptions()));
        if (callerCancels)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GenerateAsync("x", caller.Token));
        else
        {
            var error = await Assert.ThrowsAsync<AssistantException>(() => adapter.GenerateAsync("x", caller.Token));
            Assert.Equal("embedding_timeout", error.Code);
        }
    }

    [Fact]
    public async Task Extraction_parses_structured_json_without_tools_and_preserves_evidence()
    {
        var chat = new FakeChat();
        chat.EnqueueText("{\"candidates\":[{\"content\":\"Prefiero C#\",\"category\":\"Semantic\",\"sensitive\":false,\"evidence\":\"C#\",\"replaces\":[]}]}");
        var processor = new StructuredMemoryProcessor(chat, Options.Create(new AgentOptions { Model = "fake" }));
        var result = await processor.ExtractAsync("Prefiero C#", [], default);
        Assert.Equal("C#", Assert.Single(result).Evidence);
        Assert.Equal(2, Assert.Single(chat.Requests).Length);
    }

    [Fact]
    public async Task Real_chat_connector_serializes_tools_and_returns_observations_to_openrouter()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("/api/v1/chat/completions", request.RequestUri!.AbsolutePath);
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("test-model", json.RootElement.GetProperty("model").GetString());
            var toolSchema = json.RootElement.GetProperty("tools")[0].GetProperty("function");
            Assert.Equal("SearchMemory", toolSchema.GetProperty("name").GetString());
            string body;
            if (requests++ == 0)
                body = """
                {"id":"chat-1","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"SearchMemory","arguments":"{\"query\":\"lenguaje\"}"}}]},"finish_reason":"tool_calls"}]}
                """;
            else
            {
                Assert.Contains(json.RootElement.GetProperty("messages").EnumerateArray(), m =>
                    m.GetProperty("role").GetString() == "tool" && m.GetProperty("tool_call_id").GetString() == "call-1" &&
                    m.GetProperty("content").GetString()!.Contains("C#"));
                body = """
                {"id":"chat-2","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"Prefieres C#."},"finish_reason":"stop"}]}
                """;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        var provider = new OpenAIClient(new ApiKeyCredential("test-only"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1"), Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        using var chat = provider.GetChatClient("test-model").AsIChatClient();
        var steps = new List<ToolStep>();
        var answer = await new AgentRunner(chat, Options.Create(new AgentOptions { Model = "test-model" }))
            .RunAsync([new(ChatRole.User, "¿Qué lenguaje prefiero?")],
                [AIFunctionFactory.Create((string query) => new { content = "C#", query }, "SearchMemory")], steps, default);
        Assert.Equal("Prefieres C#.", answer);
        Assert.Equal(2, requests);
        Assert.Single(steps);
    }

    [Fact]
    public async Task Missing_chat_configuration_fails_before_any_network_request()
    {
        var fake = new FakeChat();
        using var client = new ConfiguredChatClient(fake, new ConfigurationBuilder().Build());
        var error = await Assert.ThrowsAsync<AssistantException>(() => client.GetResponseAsync([new(ChatRole.User, "Hola")]));
        Assert.Equal(FailureKind.Unavailable, error.Kind);
        Assert.Empty(fake.Requests);
    }
}
