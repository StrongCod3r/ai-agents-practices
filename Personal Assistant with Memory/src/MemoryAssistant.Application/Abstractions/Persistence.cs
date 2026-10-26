using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Domain.Entities;

namespace MemoryAssistant.Application.Abstractions;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record EmbeddingAvailability(bool HasActive, bool HasCompatible, bool HasPending);

public interface IMemoryRepository
{
    Task<IReadOnlyList<MemoryEntry>> ListAsync(MemoryStatus? status, CancellationToken ct);
    Task<MemoryEntry?> GetAsync(Guid id, CancellationToken ct);
    Task<MemoryEntry?> FindDuplicateAsync(string fingerprint, CancellationToken ct);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Guid>> FindRelatedToMessagesAsync(
        IReadOnlyCollection<Guid> messages,
        CancellationToken ct
    );
    Task<EmbeddingAvailability> GetEmbeddingAvailabilityAsync(
        string model,
        int dimensions,
        CancellationToken ct
    );
    Task<IReadOnlyList<MemoryHit>> SearchAsync(
        float[] vector,
        string model,
        int dimensions,
        double minSimilarity,
        int limit,
        CancellationToken ct
    );
    void Add(MemoryEntry memory);
    void RemoveRange(IEnumerable<MemoryEntry> memories);
}

public interface IConversationRepository
{
    Task<Conversation?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken ct);
    Task<ConversationMessage?> GetMessageAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ConversationMessage>> GetMessagesInSourceTurnsAsync(
        IReadOnlyCollection<Guid> sourceIds,
        CancellationToken ct
    );
    Task<IReadOnlyList<MessageView>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken ct
    );
    void Add(Conversation conversation);
    void AddMessage(ConversationMessage message);
    void Remove(Conversation conversation);
}

public interface IDataStoreHealth
{
    Task<bool> IsReadyAsync(CancellationToken ct);
}
