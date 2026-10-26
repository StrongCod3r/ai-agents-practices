using MemoryAssistant.Api.Endpoints;
using MemoryAssistant.Api.Middleware;
using MemoryAssistant.Application.Services;
using MemoryAssistant.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var migrateOnly = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--migrate").ToArray());
builder.WebHost.UseUrls("http://localhost:5281");
builder.Services.AddSerilog(
    (services, logger) =>
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override(
                "Microsoft.EntityFrameworkCore",
                Serilog.Events.LogEventLevel.Fatal
            )
            .MinimumLevel.Override(
                "System.Net.Http.HttpClient",
                Serilog.Events.LogEventLevel.Warning
            )
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}"
            )
);
builder.Services.AddOpenApi();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o =>
    o.ThrowOnBadRequest = true
);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<LocalExecutionGate>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<ConsolidationService>();
builder.Services.AddSingleton<ContextBuilder>();
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(Telemetry.Name))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(Telemetry.Name)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            tracing.AddOtlpExporter();
    });

var app = builder.Build();
if (migrateOnly)
{
    await app.Services.MigrateMemoryDatabaseAsync();
    return;
}
app.UseMiddleware<ExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles(
    new StaticFileOptions
    {
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
            context.Context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            context.Context.Response.Headers.XContentTypeOptions = "nosniff";
        },
    }
);
app.MapApiEndpoints();
app.Run();

public partial class Program;
