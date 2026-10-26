using SmartCalculator.Domain.Models;

namespace SmartCalculator.Domain.Services;

public sealed class CalculatorOperations
{
    public ToolResult Add(double a, double b)
        => Binary(a, b, () => a + b);

    public ToolResult Subtract(double a, double b)
        => Binary(a, b, () => a - b);

    public ToolResult Multiply(double a, double b)
        => Binary(a, b, () => a * b);

    public ToolResult Divide(double a, double b)
        => Binary(a, b, () => a / b, b == 0 ? "No se puede dividir entre cero." : null);

    public ToolResult Power(double a, double b)
        => Binary(a, b, () => Math.Pow(a, b));

    public ToolResult SquareRoot(double x)
    {
        if (!double.IsFinite(x)) return ToolResult.Fail("El argumento debe ser finito.");
        return x < 0 ? ToolResult.Fail("No existe raíz cuadrada real de un número negativo.") : Finite(Math.Sqrt(x));
    }

    private static ToolResult Binary(double a, double b, Func<double> calculate, string? error = null)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) return ToolResult.Fail("Los argumentos deben ser finitos.");
        return error is null ? Finite(calculate()) : ToolResult.Fail(error);
    }

    private static ToolResult Finite(double value) => double.IsFinite(value)
        ? ToolResult.Ok(value)
        : ToolResult.Fail("El cálculo no produce un número real finito (dominio inválido o desbordamiento).");
}
