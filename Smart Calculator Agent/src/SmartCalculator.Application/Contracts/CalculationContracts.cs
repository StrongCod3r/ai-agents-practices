using System.Text.Json;
using SmartCalculator.Domain.Models;
namespace SmartCalculator.Application.Contracts;

public sealed record CalculateResponse(string Answer, string Model, IReadOnlyList<CalculationStep> Steps);

public sealed record CalculationStep(int Order, string Tool, JsonElement Arguments, ToolResult Result);

