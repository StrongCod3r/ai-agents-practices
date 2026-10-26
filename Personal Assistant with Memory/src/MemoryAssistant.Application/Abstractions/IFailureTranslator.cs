using MemoryAssistant.Application.Exceptions;

namespace MemoryAssistant.Application.Abstractions;

// Infrastructure translates provider/storage failures without exposing vendor exception types.
public interface IFailureTranslator
{
    AssistantException? Translate(Exception exception);
}
