using SmartCalculator.Domain.Services;

namespace SmartCalculator.Tests;

public class CalculatorOperationsTests
{
    private readonly CalculatorOperations calculator = new();

    [Fact]
    public void PerformsAllSixOperations()
    {
        Assert.Equal(12, calculator.Add(7, 5).Value);
        Assert.Equal(-2, calculator.Subtract(3, 5).Value);
        Assert.Equal(42, calculator.Multiply(6, 7).Value);
        Assert.Equal(2.5, calculator.Divide(5, 2).Value);
        Assert.Equal(32, calculator.Power(2, 5).Value);
        Assert.Equal(9, calculator.SquareRoot(81).Value);
    }

    [Fact]
    public void ReturnsStructuredErrorsForInvalidCalculations()
    {
        var results = new[]
        {
            calculator.Divide(4, 0), calculator.SquareRoot(-1), calculator.Power(-2, 0.5),
            calculator.Multiply(double.MaxValue, 2), calculator.Add(double.NaN, 1),
            calculator.Subtract(1, double.PositiveInfinity), calculator.Power(0, -1)
        };
        Assert.All(results, result =>
        {
            Assert.False(result.Success);
            Assert.Null(result.Value);
            Assert.NotEmpty(result.Error!);
        });
    }
}
