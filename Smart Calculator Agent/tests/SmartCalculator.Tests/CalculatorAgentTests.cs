using SmartCalculator.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SmartCalculator.Application.Configuration;
using SmartCalculator.Application.Contracts;
using SmartCalculator.Application.Services;

namespace SmartCalculator.Tests;

public class CalculatorAgentTests
{
    [Fact]
    public async Task ChainsDependentCalculationsAndReturnsTrace()
    {
        var chat = new ScriptedChat(
            (_, _) => Call("Add", new() { ["a"] = 5, ["b"] = 3 }, "Primero sumaré."),
            (history, _) =>
            {
                Assert.Contains("\"value\":8", Observation(history));
                Assert.Equal(AuthorRole.Tool, history.Last().Role);
                return Call("Multiply", new() { ["a"] = 8, ["b"] = 2 });
            },
            (history, _) =>
            {
                Assert.Contains("\"value\":16", Observation(history));
                return new(AuthorRole.Assistant, "El resultado es 16.");
            });
        var response = await Agent(chat).CalculateAsync("Suma 5 y 3 y multiplica el resultado por 2.");
        Assert.Equal("El resultado es 16.", response.Answer);
        Assert.Equal(new double?[] { 8, 16 }, response.Steps.Select(x => x.Result.Value));
        Assert.Equal(new[] { 1, 2 }, response.Steps.Select(x => x.Order));
        Assert.Equal(8, response.Steps[1].Arguments.GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task HandlesMultipleCallsInOneTurn()
    {
        var chat = new ScriptedChat(
            (_, _) =>
            {
                var reply = Call("Add", new() { ["a"] = 2, ["b"] = 3 });
                reply.Items.Add(new FunctionCallContent("SquareRoot", "Calculator", "second", new() { ["x"] = 81 }));
                return reply;
            },
            (history, _) =>
            {
                Assert.Equal(2, history.Count(x => x.Role == AuthorRole.Tool));
                return new(AuthorRole.Assistant, "5 y 9.");
            });
        var response = await Agent(chat).CalculateAsync("Calcula 2 + 3 y la raíz de 81.");
        Assert.Equal(new double?[] { 5, 9 }, response.Steps.Select(x => x.Result.Value));
    }

    [Fact]
    public async Task AllowsConceptualAnswersWithoutTools()
    {
        var response = await Agent(new ScriptedChat((_, _) => new(AuthorRole.Assistant, "Sumar combina cantidades.")))
            .CalculateAsync("¿Qué significa sumar?");
        Assert.Empty(response.Steps);
    }

    [Theory]
    [InlineData("Divide", true)]
    [InlineData("Unknown", false)]
    public async Task FeedsToolErrorsBackToModel(string tool, bool validArguments)
    {
        var chat = new ScriptedChat(
            (_, _) => Call(tool, validArguments ? new() { ["a"] = 1, ["b"] = 0 } : new()),
            (history, _) =>
            {
                Assert.Contains("\"success\":false", Observation(history));
                return new(AuthorRole.Assistant, "No se pudo realizar la operación.");
            });
        var response = await Agent(chat).CalculateAsync("Divide entre cero.");
        Assert.False(Assert.Single(response.Steps).Result.Success);
    }

    [Fact]
    public async Task FeedsMissingArgumentsBackToModel()
    {
        var chat = new ScriptedChat(
            (_, _) => Call("Add", new() { ["a"] = 2 }),
            (history, _) =>
            {
                Assert.Contains("\"success\":false", Observation(history));
                return new(AuthorRole.Assistant, "Falta el segundo sumando.");
            });
        var response = await Agent(chat).CalculateAsync("Suma 2.");
        Assert.False(Assert.Single(response.Steps).Result.Success);
    }

    [Fact]
    public async Task StopsAtRoundLimit()
    {
        var chat = new ScriptedChat((_, _) => Call("Add", new() { ["a"] = 1, ["b"] = 2 }));
        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(chat, new() { MaxRounds = 1 }).CalculateAsync("Repite."));
    }

    [Fact]
    public async Task RejectsBatchOverToolBudgetBeforeInvoking()
    {
        var chat = new ScriptedChat((_, _) =>
        {
            var reply = Call("Add", new() { ["a"] = 1, ["b"] = 2 });
            reply.Items.Add(new FunctionCallContent("Add", "Calculator", "second", new() { ["a"] = 3, ["b"] = 4 }));
            return reply;
        });
        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(chat, new() { MaxToolCalls = 1 }).CalculateAsync("Dos sumas."));
    }

    [Fact]
    public async Task RejectsEmptyModelResponse()
    {
        await Assert.ThrowsAsync<InvalidModelResponseException>(() =>
            Agent(new ScriptedChat((_, _) => new(AuthorRole.Assistant, ""))).CalculateAsync("Hola"));
    }

    [Fact]
    public async Task HonorsCancellationBeforeCallingModel()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Agent(new ScriptedChat()).CalculateAsync("Hola", cancellation.Token));
    }

    private static string? Observation(ChatHistory history)
        => history.Last().Items.OfType<FunctionResultContent>().Single().Result?.ToString();

    private static CalculatorAgent Agent(IChatCompletionService chat, AgentOptions? options = null)
        => new(new SemanticKernelSessionFactory(chat), Options.Create(options ?? new()));

    private static ChatMessageContent Call(string name, KernelArguments arguments, string? text = null)
    {
        var reply = new ChatMessageContent(AuthorRole.Assistant, text);
        reply.Items.Add(new FunctionCallContent(name, "Calculator", "call-id", arguments));
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
