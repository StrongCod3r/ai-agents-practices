using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MemoryAssistant.Infrastructure.Persistence;

public sealed class EfConversationRepository(MemoryDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct) =>
        db.Conversations.Include(x => x.Messages).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken ct)
    {
        // Titles must never retain the content of excluded messages.
        var rows = await db
            .Conversations.Select(c => new
            {
                c.Id,
                c.CreatedAt,
                UpdatedAt = c.Messages.Select(m => (DateTimeOffset?)m.CreatedAt).Max()
                    ?? c.CreatedAt,
                FirstMessage = c
                    .Messages.Where(m => !m.Excluded && m.Role == "user")
                    .OrderBy(m => m.Sequence)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
                MessageCount = c.Messages.Count(m => !m.Excluded),
            })
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(c => new ConversationSummary(
                c.Id,
                c.CreatedAt,
                c.UpdatedAt,
                c.FirstMessage is null ? "Nueva conversación"
                    : c.FirstMessage.Length > 64 ? c.FirstMessage[..64] + "…"
                    : c.FirstMessage,
                c.MessageCount
            ))
            .ToArray();
    }

    public Task<ConversationMessage?> GetMessageAsync(Guid id, CancellationToken ct) =>
        db.Messages.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesInSourceTurnsAsync(
        IReadOnlyCollection<Guid> sourceIds,
        CancellationToken ct
    )
    {
        var turns = await db
            .Messages.Where(x => sourceIds.Contains(x.Id))
            .Select(x => x.TurnId)
            .Distinct()
            .ToListAsync(ct);
        return await db.Messages.Where(x => turns.Contains(x.TurnId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MessageView>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken ct
    ) =>
        await db
            .Messages.Where(x => x.ConversationId == conversationId && !x.Excluded)
            .OrderByDescending(x => x.Sequence)
            .Take(limit)
            .Select(x => new MessageView(
                x.Id,
                x.Sequence,
                x.Role,
                x.Content,
                x.Excluded,
                x.CreatedAt
            ))
            .ToListAsync(ct);

    public void Add(Conversation conversation) => db.Conversations.Add(conversation);

    public void AddMessage(ConversationMessage message) => db.Messages.Add(message);

    public void Remove(Conversation conversation) => db.Conversations.Remove(conversation);
}
