namespace MemoryAssistant.Application.Contracts;

public sealed record MemoryView(
    Guid Id,
    string Content,
    string Category,
    string Status,
    string Operation,
    bool Sensitive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Indexed,
    IReadOnlyList<Guid> SourceMessageIds,
    IReadOnlyList<Guid> Replaces
);

public sealed record MemoryHit(Guid Id, string Content, string Category, double Similarity);

public sealed record SearchResponse(
    IReadOnlyList<MemoryHit> Memories,
    bool Available,
    string? Warning = null
);

public sealed record ToolStep(int Order, string Tool, object? Result);

public sealed record MessageResponse(
    Guid ConversationId,
    string Answer,
    string Model,
    IReadOnlyList<MemoryHit> RetrievedMemories,
    IReadOnlyList<MemoryView> Candidates,
    IReadOnlyList<ToolStep> Steps,
    IReadOnlyList<string> Warnings
);

public sealed record MessageView(
    Guid Id,
    long Sequence,
    string Role,
    string Content,
    bool Excluded,
    DateTimeOffset CreatedAt
);

public sealed record ConversationView(
    Guid Id,
    DateTimeOffset CreatedAt,
    string? Summary,
    IReadOnlyList<MessageView> Messages
);

public sealed record ConversationSummary(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Title,
    int MessageCount
);

public sealed record MaintenanceResponse(int Processed, IReadOnlyList<string> Warnings);

public sealed record MemoryChangeResponse(MemoryView? Memory, IReadOnlyList<string> Warnings);
