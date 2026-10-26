using MemoryAssistant.Application.Abstractions;
using MemoryAssistant.Application.Exceptions;
using MemoryAssistant.Domain.Exceptions;

namespace MemoryAssistant.Api.Middleware;

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, IFailureTranslator failures)
    {
        try
        {
            await next(http);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            http.Response.StatusCode = 499;
        }
        catch (Exception exception)
        {
            var error = exception switch
            {
                AssistantException known => known,
                DomainRuleException rule => AssistantException.Invalid(rule.Message),
                BadHttpRequestException => AssistantException.Invalid(
                    "El cuerpo de la solicitud no es válido."
                ),
                _ => failures.Translate(exception)
                    ?? new AssistantException(
                        FailureKind.Internal,
                        "internal_error",
                        "No se pudo completar la solicitud."
                    ),
            };
            var status = StatusCode(error.Kind);
            logger.LogWarning(
                "Solicitud fallida {ErrorCode}, HTTP {Status}, trace {TraceId}",
                error.Code,
                status,
                http.TraceIdentifier
            );
            if (!http.Response.HasStarted)
                await Results
                    .Problem(
                        statusCode: status,
                        title: error.Code,
                        detail: error.Message,
                        extensions: new Dictionary<string, object?>
                        {
                            ["errorCode"] = error.Code,
                            ["traceId"] = http.TraceIdentifier,
                        }
                    )
                    .ExecuteAsync(http);
        }
    }

    private static int StatusCode(FailureKind kind) =>
        kind switch
        {
            FailureKind.InvalidInput => 400,
            FailureKind.NotFound => 404,
            FailureKind.Conflict => 409,
            FailureKind.BudgetExceeded => 422,
            FailureKind.ProviderFailure => 502,
            FailureKind.Unavailable => 503,
            FailureKind.Timeout => 504,
            _ => 500,
        };
}
