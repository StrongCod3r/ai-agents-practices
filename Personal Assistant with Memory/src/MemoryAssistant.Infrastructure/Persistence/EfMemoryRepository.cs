using System.Data;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace MemoryAssistant.Infrastructure.Persistence;

public sealed class EfMemoryRepository(MemoryDbContext db) : IMemoryRepository
{
    private IQueryable<MemoryEntry> Full =>
        db.Memories.Include(x => x.Sources).Include(x => x.Targets);

    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(
        MemoryStatus? status,
        CancellationToken ct
    ) =>
        await (status is null ? Full : Full.Where(x => x.Status == status))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

    public Task<MemoryEntry?> GetAsync(Guid id, CancellationToken ct) =>
        Full.SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task<MemoryEntry?> FindDuplicateAsync(string fingerprint, CancellationToken ct) =>
        Full.FirstOrDefaultAsync(
            x =>
                x.Fingerprint == fingerprint
                && (x.Status == MemoryStatus.Active || x.Status == MemoryStatus.Pending),
            ct
        );

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        db.Memories.AnyAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> FindRelatedToMessagesAsync(
        IReadOnlyCollection<Guid> messages,
        CancellationToken ct
    ) =>
        await db
            .Sources.Where(x => messages.Contains(x.MessageId))
            .Select(x => x.MemoryId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<EmbeddingAvailability> GetEmbeddingAvailabilityAsync(
        string model,
        int dimensions,
        CancellationToken ct
    )
    {
        var active = db.Memories.Where(x => x.Status == MemoryStatus.Active);
        if (!await active.AnyAsync(ct))
            return new(false, false, false);
        var compatible = await active.AnyAsync(
            x =>
                x.Embedding != null
                && x.EmbeddingModel == model
                && x.EmbeddingDimensions == dimensions,
            ct
        );
        var pending = await active.AnyAsync(
            x =>
                x.Embedding == null
                || x.EmbeddingModel != model
                || x.EmbeddingDimensions != dimensions,
            ct
        );
        return new(true, compatible, pending);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        float[] vector,
        string model,
        int dimensions,
        double minSimilarity,
        int limit,
        CancellationToken ct
    )
    {
        // Keep provider-specific SQL and parameters here. CASE protects mixed dimensions even
        // when the PostgreSQL planner reorders predicates during a partial reindex.
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Id", "Content", "Category", "Similarity" FROM (
                    SELECT "Id", "Content", "Category",
                        CASE WHEN "Embedding" IS NOT NULL AND "EmbeddingModel" = @model AND "EmbeddingDimensions" = @dimensions
                            THEN 1 - ("Embedding" <=> @vector) ELSE NULL END AS "Similarity"
                    FROM "Memories" WHERE "Status" = 'Active'
                ) AS scored
                WHERE "Similarity" >= @minimum
                ORDER BY "Similarity" DESC, "Id" LIMIT @limit
                """;
            command.Parameters.Add(new NpgsqlParameter("model", model));
            command.Parameters.Add(new NpgsqlParameter("dimensions", dimensions));
            command.Parameters.Add(
                new NpgsqlParameter("vector", new Vector(vector)) { DataTypeName = "vector" }
            );
            command.Parameters.Add(new NpgsqlParameter("minimum", minSimilarity));
            command.Parameters.Add(new NpgsqlParameter("limit", limit));
            var hits = new List<MemoryHit>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(
                    new(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetDouble(3)
                    )
                );
            return hits;
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }
    }

    public void Add(MemoryEntry memory) => db.Memories.Add(memory);

    public void RemoveRange(IEnumerable<MemoryEntry> memories) => db.Memories.RemoveRange(memories);
}
