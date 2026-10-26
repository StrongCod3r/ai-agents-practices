using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Infrastructure.Persistence;
using MemoryAssistant.Application.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pgvector;

namespace MemoryAssistant.Tests;

internal sealed class TestModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder builder, DbContext context)
    {
        base.Customize(builder, context);
        builder.Entity<MemoryEntry>().Property(x => x.Embedding).HasConversion(
            value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => value == null ? null : JsonSerializer.Deserialize<float[]>(value, (JsonSerializerOptions?)null)!);
    }
}

internal sealed class FakeEmbeddings : IEmbeddingClient
{
    public bool Fail { get; set; }
    public Task<float[]> GenerateAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Fail) throw new AssistantException(FailureKind.ProviderFailure, "test_embedding", "Simulated failure.");
        // Similar natural-language concepts deliberately share a vector in deterministic tests.
        return Task.FromResult(text.Contains("Python", StringComparison.OrdinalIgnoreCase) ? new float[] { 0, 1, 0 }
            : text.Contains("café", StringComparison.OrdinalIgnoreCase) ? new float[] { 0, 0, 1 } : new float[] { 1, 0, 0 });
    }
}

internal sealed class FakeChat : IChatClient
{
    public Queue<Func<IEnumerable<ChatMessage>, CancellationToken, Task<ChatResponse>>> Responses { get; } = new();
    public List<ChatMessage[]> Requests { get; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(messages.ToArray());
        return Responses.Count > 0 ? Responses.Dequeue()(messages, cancellationToken)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Respuesta de prueba.")));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.CompletedTask; yield break; }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
    public void EnqueueText(string text) => Responses.Enqueue((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));
    public void EnqueueTool(string name, Dictionary<string, object?> arguments) => Responses.Enqueue((_, _) => Task.FromResult(
        new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), name, arguments)]))));
}

internal sealed class FakeProcessor : IMemoryProcessor
{
    public bool Fail { get; set; }
    public IReadOnlyList<MemoryDraft>? Drafts { get; set; }
    public Task<IReadOnlyList<MemoryDraft>> ExtractAsync(string message, IReadOnlyList<MemoryView> active, CancellationToken ct)
    {
        if (Fail) throw new JsonException("Simulated malformed extraction");
        return Task.FromResult(Drafts ?? (message.Contains("Prefiero C#", StringComparison.OrdinalIgnoreCase)
            ? new MemoryDraft[] { new("Prefiero C#", "Semantic", false, "Prefiero C#", []) } : []));
    }
    public Task<IReadOnlyList<MemoryDraft>> ConsolidateAsync(IReadOnlyList<MemoryView> active, CancellationToken ct) => Task.FromResult(Drafts ?? []);
    public Task<string> SummarizeAsync(string? previous, IReadOnlyList<ConversationMessage> messages, CancellationToken ct) => Task.FromResult("Resumen de prueba");
}

internal sealed class Harness : IDisposable
{
    public MemoryDbContext Db { get; }
    public FakeEmbeddings Embeddings { get; } = new();
    public FakeChat Chat { get; } = new();
    public FakeProcessor Processor { get; } = new();
    public AgentOptions AgentOptions { get; } = new() { Model = "test-model" };
    public MemoryService Memory { get; }
    public ConversationService Conversations { get; }
    public Harness()
    {
        Db = new(new DbContextOptionsBuilder<MemoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCustomizer, TestModelCustomizer>().Options);
        Memory = new(new EfMemoryRepository(Db), new EfConversationRepository(Db), new EfUnitOfWork(Db), Embeddings, Options.Create(new EmbeddingOptions { Model = "fake", Dimensions = 3 }));
        Conversations = new(new EfConversationRepository(Db), new EfMemoryRepository(Db), new EfUnitOfWork(Db), Memory, new AgentRunner(Chat, Options.Create(AgentOptions)), Processor,
            new ContextBuilder(Options.Create(AgentOptions)), Options.Create(AgentOptions));
    }
    public async Task<ConversationMessage> SourceAsync(string text)
    {
        var conversation = new Conversation();
        var message = new ConversationMessage { ConversationId = conversation.Id, Content = text, TurnId = Guid.NewGuid(), Sequence = 1 };
        conversation.Messages.Add(message);
        Db.Conversations.Add(conversation);
        await Db.SaveChangesAsync();
        return message;
    }
    public void Dispose() => Db.Dispose();
}

internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string database = Guid.NewGuid().ToString();
    public FakeChat Chat { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MemoryDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<MemoryDbContext>>();
            services.AddDbContext<MemoryDbContext>(o => o.UseInMemoryDatabase(database).ReplaceService<IModelCustomizer, TestModelCustomizer>());
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(Chat);
            services.RemoveAll<IEmbeddingClient>();
            services.AddSingleton<IEmbeddingClient>(new FakeEmbeddings { Fail = true });
            services.RemoveAll<IMemoryProcessor>();
            services.AddScoped<IMemoryProcessor, FakeProcessor>();
            services.Configure<AgentOptions>(o => o.Model = "test-model");
        });
    }
}
