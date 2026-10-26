namespace MemoryAssistant.Domain.Exceptions;

public sealed class DomainRuleException(string message) : Exception(message)
{
    public static DomainRuleException Invalid(string message) => new(message);
}
