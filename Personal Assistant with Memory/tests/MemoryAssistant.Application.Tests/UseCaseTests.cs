using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Application.Services;
using MemoryAssistant.Application.Tools;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace MemoryAssistant.Application.Tests;

public sealed class UseCaseTests
{
    private sealed class Embeddings : IEmbeddingClient
    {
        public Task<float[]> GenerateAsync(string text, CancellationToken ct) => Task.FromResult(text.Contains("Python") ? new float[] { 0, 1 } : new float[] { 1, 0 });
    }
    private sealed class Processor : IMemoryProcessor
    {
        public Task<IReadOnlyList<MemoryDraft>> ExtractAsync(string message, IReadOnlyList<MemoryView> active, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryDraft>>(message == "Prefiero C#" ? [new(message, "Semantic", false, message, [])] : []);
        public Task<IReadOnlyList<MemoryDraft>> ConsolidateAsync(IReadOnlyList<MemoryView> active, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryDraft>>([]);
        public Task<string> SummarizeAsync(string? previous, IReadOnlyList<ConversationMessage> messages, CancellationToken ct) => Task.FromResult("Resumen");
    }
    private sealed class Runner : IAgentRunner
    {
        public List<PromptMessage> LastMessages { get; private set; } = [];
        public Task<string> RunAsync(List<PromptMessage> messages, IConversationTools tools, List<ToolStep> steps, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); LastMessages = messages; return Task.FromResult("Respuesta independiente del proveedor."); }
    }
    private static MemoryService Memory(InMemoryStore store) => new(store, store, store, new Embeddings(),
        Options.Create(new EmbeddingOptions { Model = "test", Dimensions = 2 }));
    private static ConversationService Conversations(InMemoryStore store, MemoryService memory, Runner runner)
    {
        var options = Options.Create(new AgentOptions { Model = "test" });
        return new(store, store, store, memory, runner, new Processor(), new ContextBuilder(options), options);
    }

    [Fact]
    public async Task Full_memory_lifecycle_runs_without_a_database_or_AI_SDK()
    {
        var store = new InMemoryStore();
        var memory = Memory(store);
        var runner = new Runner();
        var conversations = Conversations(store, memory, runner);
        var first = await conversations.CreateAsync(default);
        var response = await conversations.SendAsync(first.Id, "Prefiero C#", default);
        var candidate = Assert.Single(response.Candidates);
        Assert.Empty(await memory.ListAsync("Active", default));
        await memory.ApproveAsync(candidate.Id, candidate.Version, false, default);
        var second = await conversations.CreateAsync(default);
        var recalled = await conversations.SendAsync(second.Id, "¿Qué lenguaje prefiero?", default);
        Assert.Equal(candidate.Id, Assert.Single(recalled.RetrievedMemories).Id);
        Assert.Contains(runner.LastMessages, m => m.Text.Contains("Prefiero C#"));
        var approved = await memory.GetViewAsync(candidate.Id, default);
        var corrected = await memory.CorrectExplicitAsync(approved.Id, "Prefiero Python", approved.Version, "Semantic", false, false, default);
        Assert.Equal("Prefiero Python", corrected.Memory!.Content);
        await memory.ForgetAsync(corrected.Memory.Id, default);
        Assert.Empty(store.Memories);
        Assert.All(store.Messages, message => Assert.True(message.Excluded));
        Assert.Empty((await memory.SearchAsync("Python", default)).Memories);
    }

    [Fact]
    public async Task Sensitive_explicit_save_needs_separate_consent()
    {
        var store = new InMemoryStore();
        var memory = Memory(store);
        var result = await memory.SaveExplicitAsync("Tengo diabetes", "Semantic", false, false, default);
        Assert.Equal("Pending", result.Memory!.Status);
        var error = await Assert.ThrowsAsync<AssistantException>(() => memory.ApproveAsync(result.Memory.Id, result.Memory.Version, false, default));
        Assert.Equal(FailureKind.Conflict, error.Kind);
        Assert.Equal("sensitive_confirmation_required", error.Code);
        await memory.ApproveAsync(result.Memory.Id, result.Memory.Version, true, default);
        Assert.Single(await memory.ListAsync("Active", default));
    }

    [Fact]
    public async Task Tool_port_can_propose_but_cannot_activate_or_delete_memories()
    {
        var store = new InMemoryStore();
        var memory = Memory(store);
        var conversation = new Conversation();
        var message = new ConversationMessage { ConversationId = conversation.Id, TurnId = Guid.NewGuid(), Content = "Prefiero C#", Sequence = 1 };
        conversation.Messages.Add(message); store.Add(conversation);
        IConversationTools tools = new MemoryTools(memory, store, conversation.Id, message.Id);
        var proposal = await tools.SaveMemory("Prefiero C#", "Semantic", false, default);
        Assert.Equal("Pending", proposal.Status);
        Assert.Empty(await memory.ListAsync("Active", default));
        await memory.ApproveAsync(proposal.Id, proposal.Version, false, default);
        var deletion = await tools.DeleteMemory(proposal.Id, default);
        Assert.Equal("Delete", deletion.Operation);
        Assert.Equal("Pending", deletion.Status);
        Assert.Single(await memory.ListAsync("Active", default));
    }

    [Fact]
    public async Task Domain_rejects_credentials_even_from_an_explicit_use_case()
    {
        var store = new InMemoryStore();
        await Assert.ThrowsAsync<DomainRuleException>(() => Memory(store).SaveExplicitAsync("api_key: secret", "Semantic", true, true, default));
        Assert.Empty(store.Memories);
    }

    [Fact]
    public async Task Cancelling_before_a_turn_does_not_write_messages()
    {
        var store = new InMemoryStore();
        var conversations = Conversations(store, Memory(store), new Runner());
        var conversation = await conversations.CreateAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversations.SendAsync(conversation.Id, "Hola", new CancellationToken(true)));
        Assert.Empty(store.Messages);
    }
}
