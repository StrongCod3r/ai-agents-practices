namespace SmartCalculator.Domain.Models;

public sealed record ToolResult(bool Success, double? Value, string? Error)
{
    public static ToolResult Ok(double value) => new(true, value, null);
    public static ToolResult Fail(string error) => new(false, null, error);
}
