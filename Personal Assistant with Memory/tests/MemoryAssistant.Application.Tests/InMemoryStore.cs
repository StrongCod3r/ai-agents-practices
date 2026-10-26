using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Domain.Entities;

namespace MemoryAssistant.Application.Tests;

// Deliberately no persistence framework: tests exercise the same application ports
// that a different storage adapter would implement.
internal sealed class InMemoryStore : IMemoryRepository, IConversationRepository, IUnitOfWork
{
    public List<Conversation> Conversations { get; } = [];
    public List<MemoryEntry> Memories { get; } = [];
    public IEnumerable<ConversationMessage> Messages => Conversations.SelectMany(c => c.Messages);
    public Task SaveChangesAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    Task<MemoryEntry?> IMemoryRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Memories.SingleOrDefault(m => m.Id == id));
    Task<Conversation?> IConversationRepository.GetAsync(Guid id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Conversations.SingleOrDefault(c => c.Id == id)); }
    public Task<IReadOnlyList<MemoryEntry>> ListAsync(MemoryStatus? status, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>(Memories.Where(m => status is null || m.Status == status).ToArray());
    public Task<MemoryEntry?> FindDuplicateAsync(string fingerprint, CancellationToken ct) =>
        Task.FromResult(Memories.FirstOrDefault(m => m.Fingerprint == fingerprint && m.Status is MemoryStatus.Active or MemoryStatus.Pending));
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Memories.Any(m => m.Id == id));
    public Task<IReadOnlyList<Guid>> FindRelatedToMessagesAsync(IReadOnlyCollection<Guid> messages, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Memories.Where(m => m.Sources.Any(s => messages.Contains(s.MessageId))).Select(m => m.Id).ToArray());
    public Task<EmbeddingAvailability> GetEmbeddingAvailabilityAsync(string model, int dimensions, CancellationToken ct)
    {
        var active = Memories.Where(m => m.Status == MemoryStatus.Active).ToArray();
        bool Compatible(MemoryEntry m) => m.Embedding is not null && m.EmbeddingModel == model && m.EmbeddingDimensions == dimensions;
        return Task.FromResult(new EmbeddingAvailability(active.Length > 0, active.Any(Compatible), active.Any(m => !Compatible(m))));
    }
    public Task<IReadOnlyList<MemoryHit>> SearchAsync(float[] vector, string model, int dimensions, double minSimilarity, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryHit>>(Memories.Where(m => m.Status == MemoryStatus.Active && m.Embedding is not null &&
            m.EmbeddingModel == model && m.EmbeddingDimensions == dimensions)
            .Select(m => new MemoryHit(m.Id, m.Content, m.Category, Cosine(m.Embedding!, vector)))
            .Where(m => m.Similarity >= minSimilarity).OrderByDescending(m => m.Similarity).Take(limit).ToArray());
    private static double Cosine(float[] a, float[] b) => a.Zip(b).Sum(p => (double)p.First * p.Second) /
        Math.Sqrt(a.Sum(x => (double)x * x) * b.Sum(x => (double)x * x));
    public void Add(MemoryEntry memory) => Memories.Add(memory);
    public void RemoveRange(IEnumerable<MemoryEntry> memories) { foreach (var memory in memories.ToArray()) Memories.Remove(memory); }
    public Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConversationSummary>>(Conversations.Take(limit).Select(c => new ConversationSummary(c.Id, c.CreatedAt, c.CreatedAt,
            c.Messages.FirstOrDefault(m => !m.Excluded && m.Role == "user")?.Content ?? "Nueva conversación", c.Messages.Count(m => !m.Excluded))).ToArray());
    public Task<ConversationMessage?> GetMessageAsync(Guid id, CancellationToken ct) => Task.FromResult(Messages.SingleOrDefault(m => m.Id == id));
    public Task<IReadOnlyList<ConversationMessage>> GetMessagesInSourceTurnsAsync(IReadOnlyCollection<Guid> sourceIds, CancellationToken ct)
    {
        var turns = Messages.Where(m => sourceIds.Contains(m.Id)).Select(m => m.TurnId).ToHashSet();
        return Task.FromResult<IReadOnlyList<ConversationMessage>>(Messages.Where(m => turns.Contains(m.TurnId)).ToArray());
    }
    public Task<IReadOnlyList<MessageView>> GetRecentMessagesAsync(Guid conversationId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MessageView>>(Messages.Where(m => m.ConversationId == conversationId && !m.Excluded).OrderByDescending(m => m.Sequence)
            .Take(limit).Select(m => new MessageView(m.Id, m.Sequence, m.Role, m.Content, m.Excluded, m.CreatedAt)).ToArray());
    public void Add(Conversation conversation) => Conversations.Add(conversation);
    public void AddMessage(ConversationMessage message)
    {
        var conversation = Conversations.Single(c => c.Id == message.ConversationId);
        if (!conversation.Messages.Contains(message)) conversation.Messages.Add(message);
    }
    public void Remove(Conversation conversation) => Conversations.Remove(conversation);
}
