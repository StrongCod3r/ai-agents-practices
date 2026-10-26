using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartCalculator.Application.Abstractions;
using SmartCalculator.Application.Configuration;
using SmartCalculator.Application.Services;
using SmartCalculator.Domain.Models;
namespace SmartCalculator.Application.Tests;

public sealed class AgentUseCaseTests
{
    private static readonly AgentToolCall Call = new("call-1", "tools", "operation", JsonSerializer.SerializeToElement(new { a = 5, b = 3 }));
    private static CalculatorAgent Agent(FakeFactory factory, AgentOptions? options = null)
        => new(factory, Options.Create(options ?? new()));

    [Fact]
    public async Task ExecutesToolsAndRecordsPublicTraceWithoutSdk()
    {
        var session = new FakeSession(new(null, [Call]), new("respuesta", []));
        var response = await Agent(new(session)).CalculateAsync("consulta");
        Assert.Equal("respuesta", response.Answer);
        var step = Assert.Single(response.Steps);
        Assert.Equal("tools.operation", step.Tool);
        Assert.Equal(5, step.Arguments.GetProperty("a").GetInt32());
        Assert.True(step.Result.Success);
        Assert.Equal(1, session.Invocations);
        Assert.Single(session.Observations);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RejectsAnEntireBatchBeforeInvokingWhenOverBudget()
    {
        var session = new FakeSession(new AgentReply(null, [Call, Call]));
        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(new(session), new() { MaxToolCalls = 1 }).CalculateAsync("consulta"));
        Assert.Equal(0, session.Invocations);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RoundBudgetDisposesSession()
    {
        var session = new FakeSession(new AgentReply(null, [Call]));
        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(new(session), new() { MaxRounds = 1 }).CalculateAsync("consulta"));
        Assert.Equal(1, session.Invocations);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task CancellationPropagatesAndDisposesSession()
    {
        using var source = new CancellationTokenSource();
        var session = new FakeSession(new AgentReply(null, [Call])) { OnInvoke = source.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Agent(new(session)).CalculateAsync("consulta", source.Token));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task EmptyAnswerIsRejectedAndSessionDisposed()
    {
        var session = new FakeSession(new AgentReply(" ", []));
        await Assert.ThrowsAsync<InvalidModelResponseException>(() => Agent(new(session)).CalculateAsync("consulta"));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task EachRequestGetsItsOwnSession()
    {
        var factory = new FakeFactory(new FakeSession(new AgentReply("a", [])), new FakeSession(new AgentReply("b", [])));
        var agent = Agent(factory);
        Assert.Equal("a", (await agent.CalculateAsync("primera")).Answer);
        Assert.Equal("b", (await agent.CalculateAsync("segunda")).Answer);
        Assert.Equal(new[] { "primera", "segunda" }, factory.Messages);
    }

    [Fact]
    public void InnerLayersHaveNoVendorWebOrInfrastructureDependencies()
    {
        var application = typeof(CalculatorAgent).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        var domain = typeof(ToolResult).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.Contains("SmartCalculator.Domain", application);
        Assert.DoesNotContain(application.Concat(domain), x => x.Contains("SemanticKernel") || x.Contains("OpenAI") || x.Contains("AspNetCore") || x.EndsWith(".Infrastructure") || x.EndsWith(".Api"));
        Assert.DoesNotContain(domain, x => x.StartsWith("Microsoft.Extensions") || x.StartsWith("SmartCalculator."));
    }

    private sealed class FakeFactory(params FakeSession[] sessions) : IAgentSessionFactory
    {
        private readonly Queue<FakeSession> queue = new(sessions);
        public List<string> Messages { get; } = [];
        public IAgentSession Create(string instructions, string message)
        {
            Assert.False(string.IsNullOrWhiteSpace(instructions));
            Messages.Add(message);
            return queue.Dequeue();
        }
    }
    private sealed class FakeSession(params AgentReply[] replies) : IAgentSession
    {
        private readonly Queue<AgentReply> queue = new(replies);
        public int Invocations { get; private set; }
        public bool Disposed { get; private set; }
        public Action? OnInvoke { get; init; }
        public List<ToolResult> Observations { get; } = [];
        public Task<AgentReply> GetReplyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(queue.Dequeue());
        }
        public Task<ToolResult> InvokeAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            Invocations++;
            OnInvoke?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ToolResult.Ok(8));
        }
        public void RecordResult(AgentToolCall call, ToolResult result) => Observations.Add(result);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
