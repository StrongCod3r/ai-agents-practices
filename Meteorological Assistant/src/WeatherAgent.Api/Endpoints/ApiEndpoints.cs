using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using WeatherAgent.Application.Configuration;
using WeatherAgent.Application.Contracts;
using WeatherAgent.Api.Contracts;
using WeatherAgent.Application.Abstractions;
using WeatherAgent.Application.Services;

namespace WeatherAgent.Api.Endpoints;

public static class ApiEndpoints
{
    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options => options.WithTitle("Asistente meteorológico"));
        }
        app.MapGet("/", () => Results.Redirect(app.Environment.IsDevelopment() ? "/scalar/v1" : "/health")).ExcludeFromDescription();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithSummary("Comprueba que la API está en ejecución; no consulta al modelo.");

        app.MapPost("/api/weather", HandleWeatherAsync)
            .WithName("AskWeather")
            .WithSummary("Consulta el tiempo y compara ciudades en lenguaje natural.")
            .WithDescription("El modelo decide qué herramientas ejecutar y puede encadenar sus resultados. Devuelve la respuesta y los pasos con argumentos y resultados estructurados.")
            .Produces<WeatherResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(422)
            .ProducesProblem(502)
            .ProducesProblem(503)
            .ProducesProblem(504);

        return app;
    }

    private static async Task<IResult> HandleWeatherAsync(WeatherRequest request, WeatherAgentService agent,
        IOptions<AgentOptions> options, IProviderAvailability availability, IProviderFailureClassifier classifier, HttpContext context,
        ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4000)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["message"] = ["Escribe una solicitud de entre 1 y 4000 caracteres."]
            });

        if (!availability.IsConfigured)
            return Results.Problem(statusCode: 503, title: "OpenRouter no está configurado",
                detail: "Configura OPENROUTER_API_KEY en el servidor.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            return Results.Ok(await agent.AskAsync(request.Message.Trim(), timeout.Token));
        }
        catch (AgentLimitException ex)
        {
            return Results.Problem(statusCode: 422, title: "Límite de operaciones", detail: ex.Message);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 504, title: "El agente agotó el tiempo disponible");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = classifier.Classify(ex);
            logger.LogWarning("Fallo de IA: {Code}; HTTP proveedor: {ProviderStatus}; modelo: {Model}; tipo: {ExceptionType}; traceId: {TraceId}",
                failure.Code, failure.ProviderStatus, options.Value.Model, ex.GetType().Name,
                System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier);
            return Results.Problem(statusCode: 502, title: failure.Title, detail: failure.Detail,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = failure.Code,
                    ["providerStatus"] = failure.ProviderStatus,
                    ["model"] = options.Value.Model
                });
        }
    }
}
