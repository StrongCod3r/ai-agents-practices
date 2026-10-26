using SmartCalculator.Api.Endpoints;
using SmartCalculator.Application.Services;
using SmartCalculator.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure();
builder.Services.AddTransient<CalculatorAgent>();

var app = builder.Build();
app.UseExceptionHandler();
app.MapApiEndpoints();

app.Run();
public partial class Program;
