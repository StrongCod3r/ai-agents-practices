using System.Text.Json;
using WeatherAgent.Domain.Models;
namespace WeatherAgent.Application.Contracts;

public sealed record WeatherResponse(string Answer, string Model, IReadOnlyList<WeatherStep> Steps);
public sealed record WeatherStep(int Order, string Tool, JsonElement Arguments, ToolResult Result);
