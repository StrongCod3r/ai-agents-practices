namespace MemoryAssistant.Api.Contracts;

public sealed record MessageRequest(string Message);

public sealed record CreateMemoryRequest(
    string Content,
    string Category = "Semantic",
    bool Sensitive = false,
    bool ConfirmSensitive = false
);

public sealed record UpdateMemoryRequest(
    string Content,
    int Version,
    string Category = "Semantic",
    bool Sensitive = false,
    bool ConfirmSensitive = false
);

public sealed record ApproveRequest(int Version, bool ConfirmSensitive = false);

public sealed record RejectRequest(int Version);

public sealed record SearchRequest(string Query);
