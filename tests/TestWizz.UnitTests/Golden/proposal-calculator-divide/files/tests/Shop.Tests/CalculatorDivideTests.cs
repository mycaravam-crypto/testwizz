namespace Shop.Tests;

public class CalculatorDivideTests
{
    private readonly Calculator _calculator = new();

    [Fact]
    [Trait("Scenario", "S1")]
    public void Divide_ReturnsQuotient()
    {
        Assert.Equal(4, _calculator.Divide(8, 2));
    }

    [Fact]
    [Trait("Scenario", "S2")]
    public void Divide_ByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => _calculator.Divide(1, 0));
    }

    // Review: Inferred from the current implementation.
    [Theory]
    [Trait("Scenario", "S3")]
    [InlineData(1, 1)]
    public void Divide_ByOne_ReturnsDividend(int dividend, int expected)
    {
        Assert.Equal(expected, _calculator.Divide(dividend, 1));
    }
}
