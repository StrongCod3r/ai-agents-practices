using System.ClientModel;
using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MemoryAssistant.Infrastructure.Diagnostics;

public sealed class FailureTranslator : IFailureTranslator
{
    public AssistantException? Translate(Exception exception)
    {
        if (exception is AssistantException known)
            return known;
        if (exception is DbUpdateConcurrencyException)
            return AssistantException.Conflict("El recurso cambió; vuelve a consultarlo.");
        if (exception is ClientResultException provider)
            return new(
                provider.Status is 401 or 403
                    ? FailureKind.Unavailable
                    : FailureKind.ProviderFailure,
                "chat_provider",
                $"El proveedor de chat respondió HTTP {provider.Status}."
            );
        if (exception is HttpRequestException)
            return new(
                FailureKind.ProviderFailure,
                "provider_connection",
                "No se pudo conectar con el proveedor."
            );
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is NpgsqlException)
                return new(
                    FailureKind.Unavailable,
                    "database_unavailable",
                    "PostgreSQL no está disponible; comprueba el servicio y las migraciones."
                );
        return null;
    }
}
