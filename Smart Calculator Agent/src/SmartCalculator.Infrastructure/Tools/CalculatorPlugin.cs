using SmartCalculator.Domain.Services;
using System.ComponentModel;
using Microsoft.SemanticKernel;
using SmartCalculator.Domain.Models;

namespace SmartCalculator.Infrastructure.Tools;

public sealed class CalculatorPlugin
{
    private readonly CalculatorOperations operations = new();
    [KernelFunction("Add"), Description("Suma dos números reales: a + b.")]
    public ToolResult Add([Description("Primer sumando.")] double a, [Description("Segundo sumando.")] double b)
        => operations.Add(a, b);

    [KernelFunction("Subtract"), Description("Resta b de a: a - b.")]
    public ToolResult Subtract([Description("Minuendo.")] double a, [Description("Sustraendo.")] double b)
        => operations.Subtract(a, b);

    [KernelFunction("Multiply"), Description("Multiplica dos números reales: a * b.")]
    public ToolResult Multiply([Description("Primer factor.")] double a, [Description("Segundo factor.")] double b)
        => operations.Multiply(a, b);

    [KernelFunction("Divide"), Description("Divide a entre b. El divisor no puede ser cero.")]
    public ToolResult Divide([Description("Dividendo.")] double a, [Description("Divisor distinto de cero.")] double b)
        => operations.Divide(a, b);

    [KernelFunction("Power"), Description("Eleva la base a al exponente b: a^b. Solo resultados reales finitos.")]
    public ToolResult Power([Description("Base.")] double a, [Description("Exponente.")] double b)
        => operations.Power(a, b);

    [KernelFunction("SquareRoot"), Description("Calcula la raíz cuadrada real de x. x debe ser mayor o igual que cero.")]
    public ToolResult SquareRoot([Description("Radicando no negativo.")] double x) => operations.SquareRoot(x);

}
