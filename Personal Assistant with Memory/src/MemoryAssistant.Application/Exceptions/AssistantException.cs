namespace MemoryAssistant.Application.Exceptions;

public enum FailureKind
{
    InvalidInput,
    NotFound,
    Conflict,
    BudgetExceeded,
    ProviderFailure,
    Unavailable,
    Timeout,
    Internal,
}

// Application failures describe use-case outcomes. HTTP status codes belong to the API adapter.
public sealed class AssistantException(FailureKind kind, string code, string message)
    : Exception(message)
{
    public FailureKind Kind { get; } = kind;
    public string Code { get; } = code;

    public static AssistantException Invalid(string message) =>
        new(FailureKind.InvalidInput, "invalid_input", message);

    public static AssistantException Missing() =>
        new(FailureKind.NotFound, "not_found", "No existe el recurso solicitado.");

    public static AssistantException Conflict(string message) =>
        new(FailureKind.Conflict, "version_conflict", message);
}
