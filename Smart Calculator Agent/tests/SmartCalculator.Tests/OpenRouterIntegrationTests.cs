using SmartCalculator.Infrastructure.Services;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using SmartCalculator.Application.Configuration;
using SmartCalculator.Application.Services;

namespace SmartCalculator.Tests;

public class OpenRouterIntegrationTests
{
    [Fact]
    public async Task SerializesToolsReasoningAndChainedObservationsUsingRealConnector()
    {
        using var handler = new RouterHandler();
        using var reasoning = new OpenRouterReasoningHandler(Options.Create(new AgentOptions())) { InnerHandler = handler };
        using var http = new HttpClient(reasoning);
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1"),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        var agent = new CalculatorAgent(new SemanticKernelSessionFactory(new OpenAIChatCompletionService("test-model", client)),
            Options.Create(new AgentOptions { Model = "test-model" }));
        var result = await agent.CalculateAsync("Suma 5 y 3; multiplica por 2.");
        Assert.Equal(new double?[] { 8, 16 }, result.Steps.Select(x => x.Result.Value));
        Assert.Equal(3, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0]);
        Assert.True(first.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.Equal(6, first.RootElement.GetProperty("tools").GetArrayLength());
        var add = first.RootElement.GetProperty("tools").EnumerateArray()
            .Single(x => x.GetProperty("function").GetProperty("name").GetString() == "Calculator-Add")
            .GetProperty("function");
        Assert.Equal("number", add.GetProperty("parameters").GetProperty("properties").GetProperty("a").GetProperty("type").GetString());
        using var second = JsonDocument.Parse(handler.Requests[1]);
        var observation = second.RootElement.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("tool", observation.GetProperty("role").GetString());
        Assert.Equal("call-1", observation.GetProperty("tool_call_id").GetString());
        Assert.Contains("\"value\":8", observation.GetProperty("content").GetString());
    }

    private sealed class RouterHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var turn = Requests.Count;
            object message = turn switch
            {
                1 => new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call-1", type = "function", function = new { name = "Calculator-Add", arguments = "{\"a\":5,\"b\":3}" } } } },
                2 => new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call-2", type = "function", function = new { name = "Calculator-Multiply", arguments = "{\"a\":8,\"b\":2}" } } } },
                _ => new { role = "assistant", content = "El resultado es 16." }
            };
            var body = JsonSerializer.Serialize(new
            {
                id = $"chat-{turn}",
                @object = "chat.completion",
                created = 1700000000,
                model = "test-model",
                choices = new[] { new { index = 0, message, finish_reason = turn < 3 ? "tool_calls" : "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
