using MemoryAssistant.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MemoryAssistant.Infrastructure.Persistence;

public sealed class DataStoreHealth(MemoryDbContext db) : IDataStoreHealth
{
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            if (
                !await db.Database.CanConnectAsync(ct)
                || !await db
                    .Database.SqlQueryRaw<int>(
                        "SELECT 1 AS \"Value\" FROM pg_extension WHERE extname = 'vector'"
                    )
                    .AnyAsync(ct)
            )
                return false;
            _ = await db.Conversations.AnyAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            return false;
        }
    }
}
