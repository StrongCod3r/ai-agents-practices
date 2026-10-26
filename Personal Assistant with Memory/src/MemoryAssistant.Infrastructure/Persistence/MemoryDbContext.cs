using MemoryAssistant.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MemoryAssistant.Infrastructure.Persistence;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<MemoryEntry> Memories => Set<MemoryEntry>();
    public DbSet<MemorySource> Sources => Set<MemorySource>();
    public DbSet<MemoryTarget> Targets => Set<MemoryTarget>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");
        model
            .Entity<Conversation>()
            .HasMany(x => x.Messages)
            .WithOne()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        model
            .Entity<ConversationMessage>()
            .HasIndex(x => new { x.ConversationId, x.Sequence })
            .IsUnique();
        model.Entity<ConversationMessage>().Property(x => x.Role).HasMaxLength(20);
        var memory = model.Entity<MemoryEntry>();
        memory
            .Property(x => x.Embedding)
            .HasConversion<EmbeddingVectorConverter>()
            .HasColumnType("vector")
            .Metadata.SetValueComparer(
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<float[]?>(
                    (left, right) =>
                        left == null ? right == null : right != null && left.SequenceEqual(right),
                    values =>
                        values == null
                            ? 0
                            : values.Aggregate(0, (hash, value) => HashCode.Combine(hash, value)),
                    values => values == null ? null : values.ToArray()
                )
            );
        memory.Property(x => x.Version).IsConcurrencyToken();
        memory.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        memory.Property(x => x.Operation).HasConversion<string>().HasMaxLength(20);
        memory.Property(x => x.Category).HasMaxLength(20);
        memory.Property(x => x.Fingerprint).HasMaxLength(64);
        memory.HasIndex(x => new { x.Status, x.Fingerprint });
        memory
            .HasMany(x => x.Sources)
            .WithOne()
            .HasForeignKey(x => x.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
        memory
            .HasMany(x => x.Targets)
            .WithOne()
            .HasForeignKey(x => x.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<MemorySource>().HasKey(x => new { x.MemoryId, x.MessageId });
        model
            .Entity<MemorySource>()
            .HasOne<ConversationMessage>()
            .WithMany()
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<MemoryTarget>().HasKey(x => new { x.MemoryId, x.TargetId });
        // Target ids remain as provenance until the entire related lineage is forgotten.
        model.Entity<MemoryTarget>().HasIndex(x => x.TargetId);
    }
}
