using WeatherAgent.Application.Tools;
using WeatherAgent.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using WeatherAgent.Application.Configuration;
using WeatherAgent.Application.Services;
using WeatherAgent.Infrastructure.Tools;

namespace WeatherAgent.Tests;

public class WeatherAgentTests
{
    [Fact]
    public async Task ChainsLocationAndWeatherAndReturnsStructuredTrace()
    {
        var chat = new ScriptedChat(
            (_, _) => Call("ResolveLocation", new() { ["city"] = "Madrid, España" }),
            (history, _) =>
            {
                Assert.Contains("40.4", Observation(history));
                return Call("GetCurrentWeather", new() { ["latitude"] = 40.4, ["longitude"] = -3.7 });
            },
            (history, _) =>
            {
                Assert.Contains("\"temperatureC\":20", Observation(history));
                return new(AuthorRole.Assistant, "En Madrid hace 20 °C, según Open-Meteo.");
            });
        var result = await Agent(chat).AskAsync("¿Qué tiempo hace en Madrid?");
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(new[] { 1, 2 }, result.Steps.Select(x => x.Order));
        Assert.All(result.Steps, x => Assert.True(x.Result.Success));
        Assert.Equal(40.4, result.Steps[1].Arguments.GetProperty("latitude").GetDouble());
    }

    [Fact]
    public async Task AllowsConceptualAnswersAndKeepsHistoryIsolated()
    {
        var chat = new ScriptedChat((history, _) =>
        {
            Assert.Equal(2, history.Count);
            Assert.Contains("2026-10-15", history[0].Content);
            return new(AuthorRole.Assistant, "La lluvia se mide en milímetros.");
        }, (history, _) =>
        {
            Assert.Equal(2, history.Count);
            return new(AuthorRole.Assistant, "Indica tu ciudad.");
        });
        var agent = Agent(chat);
        Assert.Empty((await agent.AskAsync("¿Cómo se mide la lluvia?")).Steps);
        Assert.Empty((await agent.AskAsync("¿Puedo correr hoy?")).Steps);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("GetCurrentWeather")]
    public async Task ReturnsInvalidToolOrMissingArgumentsToModel(string tool)
    {
        var result = await Agent(new ScriptedChat((_, _) => Call(tool, new()), (history, _) =>
        {
            Assert.Contains("\"success\":false", Observation(history));
            return new(AuthorRole.Assistant, "Necesito una ubicación válida.");
        })).AskAsync("Tiempo");
        Assert.False(Assert.Single(result.Steps).Result.Success);
    }

    [Fact]
    public async Task LimitsRoundsAndToolsAndRejectsEmptyAnswers()
    {
        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(new ScriptedChat((_, _) => Call("ResolveLocation", new() { ["city"] = "Madrid" })),
            new() { MaxRounds = 1 }).AskAsync("Tiempo"));
        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(new ScriptedChat((_, _) =>
        {
            var reply = Call("ResolveLocation", new() { ["city"] = "Madrid" });
            reply.Items.Add(new FunctionCallContent("ResolveLocation", "Weather", "second", new() { ["city"] = "Paris" }));
            return reply;
        }), new() { MaxToolCalls = 1 }).AskAsync("Compara"));
        await Assert.ThrowsAsync<InvalidModelResponseException>(() => Agent(new ScriptedChat((_, _) => new(AuthorRole.Assistant, ""))).AskAsync("Hola"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Agent(new ScriptedChat()).AskAsync("Hola", cancellation.Token));
    }

    private static WeatherAgentService Agent(IChatCompletionService chat, AgentOptions? options = null)
        => new(new SemanticKernelSessionFactory(chat, new WeatherPlugin(new WeatherTools(WeatherToolsTests.Create(request =>
            WeatherToolsTests.Json(request.RequestUri!.Host.StartsWith("geocoding") ? WeatherToolsTests.Locations() : WeatherToolsTests.Current()))))),
            Options.Create(options ?? new()), new FixedClock());
    private static string? Observation(ChatHistory history) => history.Last().Items.OfType<FunctionResultContent>().Single().Result?.ToString();
    internal static ChatMessageContent Call(string name, KernelArguments args)
    {
        var reply = new ChatMessageContent(AuthorRole.Assistant, content: null);
        reply.Items.Add(new FunctionCallContent(name, "Weather", Guid.NewGuid().ToString(), args));
        return reply;
    }
}

internal sealed class ScriptedChat(params Func<ChatHistory, Kernel?, ChatMessageContent>[] replies) : IChatCompletionService
{
    private readonly Queue<Func<ChatHistory, Kernel?, ChatMessageContent>> queue = new(replies);
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();
    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory history,
        PromptExecutionSettings? settings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.NotNull(settings?.FunctionChoiceBehavior);
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>([queue.Dequeue()(history, kernel)]);
    }
    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory history,
        PromptExecutionSettings? settings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
