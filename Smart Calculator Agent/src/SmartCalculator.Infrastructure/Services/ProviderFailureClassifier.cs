using SmartCalculator.Application.Services;
using SmartCalculator.Application.Abstractions;
using System.ClientModel;
using Microsoft.SemanticKernel;

namespace SmartCalculator.Infrastructure.Services;

public sealed class ProviderFailureClassifier : IProviderFailureClassifier
{
    public ProviderFailure Classify(Exception exception) => FromException(exception);

    public static ProviderFailure FromException(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            int? status = cause switch
            {
                HttpOperationException operation => (int?)operation.StatusCode,
                ClientResultException client => client.Status,
                HttpRequestException request => (int?)request.StatusCode,
                _ => null
            };
            if (status is not null)
                return status switch
                {
                    401 or 403 => new("provider_authentication", "OpenRouter rechazó la autenticación",
                        "Comprueba OPENROUTER_API_KEY y los permisos de la clave; reinicia el servidor después de cambiarla.", status),
                    402 => new("provider_credits", "OpenRouter requiere créditos",
                        "Comprueba el saldo y las restricciones de la cuenta en OpenRouter.", status),
                    404 => new("model_unavailable", "El modelo no tiene endpoints disponibles en OpenRouter",
                        "Selecciona un modelo disponible con soporte de tools en Agent:Model (variable Agent__Model) y reinicia el servidor.", status),
                    429 => new("provider_rate_limit", "OpenRouter ha limitado las solicitudes",
                        "Se ha alcanzado una cuota o límite de solicitudes. Espera antes de reintentar y consulta los límites de tu cuenta.", status),
                    400 or 422 => new("provider_request_rejected", "OpenRouter rechazó los parámetros de la solicitud",
                        "Comprueba que el modelo admite tools y reasoning; puedes desactivar Agent:ReasoningEnabled.", status),
                    _ => new("provider_unavailable", "El proveedor de IA no pudo completar la solicitud",
                        "El proveedor devolvió un error. Reintenta más tarde o selecciona otro modelo disponible con soporte de tools.", status)
                };
            if (cause is HttpRequestException)
                return new("provider_connection", "No se pudo conectar con OpenRouter",
                    "Comprueba la conexión a Internet, DNS y el acceso HTTPS a openrouter.ai desde el servidor.", null);
        }
        return exception is InvalidModelResponseException
            ? new("invalid_model_response", "El modelo devolvió una respuesta vacía", "Reintenta o selecciona otro modelo con soporte de tools.", null)
            : new("agent_failure", "No se pudo completar la solicitud al modelo",
                "Consulta el registro del servidor con el traceId de esta respuesta.", null);
    }
}
