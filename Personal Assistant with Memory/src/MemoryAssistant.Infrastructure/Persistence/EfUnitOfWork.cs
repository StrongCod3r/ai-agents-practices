using MemoryAssistant.Application.Abstractions;

namespace MemoryAssistant.Infrastructure.Persistence;

public sealed class EfUnitOfWork(MemoryDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
}
