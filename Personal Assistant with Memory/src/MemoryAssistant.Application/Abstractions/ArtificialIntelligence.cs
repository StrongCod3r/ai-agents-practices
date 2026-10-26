using MemoryAssistant.Application.Contracts;
using MemoryAssistant.Domain.Entities;

namespace MemoryAssistant.Application.Abstractions;

public sealed record PromptMessage(string Role, string Text);

public sealed record MemoryDraft(
    string Content,
    string Category,
    bool Sensitive,
    string Evidence,
    Guid[]? Replaces
);

public interface IEmbeddingClient
{
    Task<float[]> GenerateAsync(string text, CancellationToken ct);
}

public interface IMemoryProcessor
{
    Task<IReadOnlyList<MemoryDraft>> ExtractAsync(
        string message,
        IReadOnlyList<MemoryView> active,
        CancellationToken ct
    );
    Task<IReadOnlyList<MemoryDraft>> ConsolidateAsync(
        IReadOnlyList<MemoryView> active,
        CancellationToken ct
    );
    Task<string> SummarizeAsync(
        string? previous,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken ct
    );
}

public interface IConversationTools
{
    HashSet<Guid> UsedMemoryIds { get; }
    Task<SearchResponse> SearchMemory(string query, CancellationToken ct);
    Task<MemoryView> SaveMemory(
        string content,
        string category,
        bool sensitive,
        CancellationToken ct
    );
    Task<MemoryView> UpdateMemory(Guid id, string content, bool sensitive, CancellationToken ct);
    Task<MemoryView> DeleteMemory(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MessageView>> GetConversationHistory(CancellationToken ct);
}

public interface IAgentRunner
{
    Task<string> RunAsync(
        List<PromptMessage> messages,
        IConversationTools tools,
        List<ToolStep> steps,
        CancellationToken ct
    );
}
