namespace MemoryAssistant.Domain.Entities;

public enum MemoryStatus
{
    Pending,
    Active,
    Superseded,
    Rejected,
}

public enum MemoryOperation
{
    Create,
    Replace,
    Delete,
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Summary { get; set; }
    public long SummaryThroughSequence { get; set; }
    public List<ConversationMessage> Messages { get; set; } = [];
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public long Sequence { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public bool Excluded { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Content { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Category { get; set; } = "Semantic";
    public MemoryStatus Status { get; set; } = MemoryStatus.Pending;
    public MemoryOperation Operation { get; set; } = MemoryOperation.Create;
    public bool Sensitive { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public float[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDimensions { get; set; }
    public List<MemorySource> Sources { get; set; } = [];
    public List<MemoryTarget> Targets { get; set; } = [];
}

public sealed class MemorySource
{
    public Guid MemoryId { get; set; }
    public Guid MessageId { get; set; }
}

public sealed class MemoryTarget
{
    public Guid MemoryId { get; set; }
    public Guid TargetId { get; set; }
    public int TargetVersion { get; set; }
}
