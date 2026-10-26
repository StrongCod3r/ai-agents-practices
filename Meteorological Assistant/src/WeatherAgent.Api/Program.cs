using WeatherAgent.Api.Endpoints;
using WeatherAgent.Application.Services;
using WeatherAgent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure();
builder.Services.AddTransient<WeatherAgentService>();

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode : StatusCodes.Status500InternalServerError
});
app.MapApiEndpoints();

app.Run();
public partial class Program;
