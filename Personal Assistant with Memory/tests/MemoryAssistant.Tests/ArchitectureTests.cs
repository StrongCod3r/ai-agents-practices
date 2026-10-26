using MemoryAssistant.Application.Services;
using MemoryAssistant.Domain.Entities;
using MemoryAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MemoryAssistant.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Infrastructure_references_application_and_never_the_api()
    {
        var references = typeof(MemoryDbContext).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.Contains("MemoryAssistant.Application", references);
        Assert.DoesNotContain("MemoryAssistant.Api", references);
    }

    [Fact]
    public void Domain_vectors_stay_provider_neutral_while_database_mapping_stays_compatible()
    {
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", pg => pg.UseVector()).Options);
        var entity = db.Model.FindEntityType(typeof(MemoryEntry))!;
        var property = entity.FindProperty(nameof(MemoryEntry.Embedding))!;
        Assert.Equal(typeof(float[]), property.ClrType);
        Assert.Equal("Memories", entity.GetTableName());
        Assert.Equal("Embedding", property.GetColumnName(StoreObjectIdentifier.Table("Memories", null)));
        Assert.Equal("vector", property.GetColumnType());
        var converter = property.GetTypeMapping().Converter!;
        Assert.Equal(typeof(Vector), converter.ProviderClrType);
        var numeric = new float[] { 1, .5f, 0 };
        var persisted = Assert.IsType<Vector>(converter.ConvertToProvider(numeric));
        Assert.Equal(numeric, Assert.IsType<float[]>(converter.ConvertFromProvider(persisted)));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Single(db.Database.GetMigrations());
    }
}
