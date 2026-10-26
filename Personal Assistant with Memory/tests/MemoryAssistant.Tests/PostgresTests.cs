using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Domain.Policies;
using MemoryAssistant.Infrastructure.AI;
using MemoryAssistant.Application.Configuration;
using MemoryAssistant.Infrastructure.Persistence;
using MemoryAssistant.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;

namespace MemoryAssistant.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEMORY_TEST_CONNECTION")))
            Skip = "Set MEMORY_TEST_CONNECTION to run against PostgreSQL with pgvector and CREATE DATABASE privilege.";
    }
}

public sealed class PostgresTests : IAsyncLifetime
{
    private readonly string database = "memory_test_" + Guid.NewGuid().ToString("N");
    private string? connection;
    private readonly FakeEmbeddings embeddings = new();
    private readonly EmbeddingOptions embeddingOptions = new() { Model = "fake", Dimensions = 3, MinSimilarity = .5 };

    public async Task InitializeAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MEMORY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin)) return;
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", conn);
        await command.ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        await using var db = Open();
        await db.Database.MigrateAsync();
        NpgsqlConnection.ClearAllPools();
    }

    private MemoryDbContext Open() => new(new DbContextOptionsBuilder<MemoryDbContext>()
        .UseNpgsql(connection!, pg => pg.UseVector()).Options);
    private MemoryService Memory(MemoryDbContext db) => new(new EfMemoryRepository(db), new EfConversationRepository(db), new EfUnitOfWork(db), embeddings, Options.Create(embeddingOptions));

    [PostgresFact]
    public async Task Migrations_are_repeatable_and_history_survives_a_new_context()
    {
        Guid id;
        await using (var db = Open())
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            id = conversation.Id;
            conversation.Messages.Add(new() { ConversationId = id, Sequence = 1, Content = "Proyecto Aurora", TurnId = Guid.NewGuid() });
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }
        await using var restarted = Open();
        Assert.Equal("Proyecto Aurora", (await restarted.Messages.SingleAsync(x => x.ConversationId == id)).Content);
        Assert.False(restarted.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task Vector_search_excludes_pending_rejected_and_superseded_memories()
    {
        await using var db = Open();
        var memory = Memory(db);
        var csharp = await memory.ProposeAsync("Prefiero C#", "Semantic", false, null, null, false, default);
        await memory.ApproveAsync(csharp.Id, 1, false, default);
        var coffee = await memory.ProposeAsync("Prefiero café", "Semantic", false, null, null, false, default);
        await memory.ApproveAsync(coffee.Id, 1, false, default);
        await memory.ProposeAsync("Mi lenguaje es .NET", "Semantic", false, null, null, false, default);
        var rejected = await memory.ProposeAsync("Uso Visual Studio", "Semantic", false, null, null, false, default);
        await memory.RejectAsync(rejected.Id, 1, default);
        var search = await memory.SearchAsync("¿Qué lenguaje suelo usar?", default);
        Assert.True(search.Available);
        Assert.Equal(csharp.Id, Assert.Single(search.Memories).Id);
        var correction = await memory.ProposeAsync("Prefiero Python", "Semantic", false, null, [csharp.Id], false, default);
        await memory.ApproveAsync(correction.Id, 1, false, default);
        Assert.Empty((await memory.SearchAsync("C#", default)).Memories);
        Assert.Equal(correction.Id, Assert.Single((await memory.SearchAsync("Python", default)).Memories).Id);
    }

    [PostgresFact]
    public async Task Model_change_requires_explicit_reindex_and_query_failure_degrades_gracefully()
    {
        await using var db = Open();
        var memory = Memory(db);
        var entry = await memory.ProposeAsync("Prefiero C#", "Semantic", false, null, null, false, default);
        await memory.ApproveAsync(entry.Id, 1, false, default);
        embeddingOptions.Model = "fake-v2";
        Assert.False((await memory.SearchAsync("lenguaje", default)).Available);
        Assert.Equal(1, (await memory.ReindexAsync(default)).Processed);
        Assert.Single((await memory.SearchAsync("lenguaje", default)).Memories);
        embeddings.Fail = true;
        Assert.False((await memory.SearchAsync("lenguaje", default)).Available);
    }

    [PostgresFact]
    public async Task Full_flow_approval_cross_conversation_retrieval_correction_and_forget()
    {
        Guid originalId;
        Guid secondId;
        var chat = new FakeChat();
        var options = Options.Create(new AgentOptions { Model = "fake" });
        await using (var db = Open())
        {
            var memory = Memory(db);
            var service = new ConversationService(new EfConversationRepository(db), new EfMemoryRepository(db), new EfUnitOfWork(db), memory, new AgentRunner(chat, options), new FakeProcessor(), new ContextBuilder(options), options);
            var first = await service.CreateAsync(default);
            var response = await service.SendAsync(first.Id, "Prefiero C#", default);
            var candidate = Assert.Single(response.Candidates);
            originalId = candidate.Id;
            await memory.ApproveAsync(originalId, candidate.Version, false, default);
            secondId = (await service.CreateAsync(default)).Id;
        }
        await using (var db = Open())
        {
            var memory = Memory(db);
            var service = new ConversationService(new EfConversationRepository(db), new EfMemoryRepository(db), new EfUnitOfWork(db), memory, new AgentRunner(chat, options), new FakeProcessor(), new ContextBuilder(options), options);
            var recalled = await service.SendAsync(secondId, "¿Qué lenguaje suelo usar?", default);
            Assert.Equal(originalId, Assert.Single(recalled.RetrievedMemories).Id);
            var next = await memory.ProposeAsync("Prefiero Python", "Semantic", false, null, [originalId], false, default);
            await memory.ApproveAsync(next.Id, 1, false, default);
            await memory.ForgetAsync(next.Id, default);
        }
        await using (var db = Open())
        {
            Assert.Empty(await db.Memories.ToListAsync());
            Assert.Empty(await db.Sources.ToListAsync());
            Assert.Empty(await db.Targets.ToListAsync());
            Assert.True((await db.Messages.ToListAsync()).All(x => x.Excluded));
            Assert.Empty((await Memory(db).SearchAsync("Python", default)).Memories);
        }
    }

    public async Task DisposeAsync()
    {
        var admin = Environment.GetEnvironmentVariable("MEMORY_TEST_CONNECTION");
        if (connection is null || string.IsNullOrWhiteSpace(admin)) return;
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", conn);
        await command.ExecuteNonQueryAsync();
    }
}
