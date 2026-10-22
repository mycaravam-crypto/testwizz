namespace Shop.Tests;

public class CalculatorTests
{
    private readonly Calculator _calculator = new();

    [Fact]
    public void Add_ReturnsSum()
    {
        Assert.Equal(5, _calculator.Add(2, 3));
    }

    [Theory]
    [InlineData(10, 2, 5)]
    [InlineData(-9, 3, -3)]
    public void Divide_ReturnsQuotient(int dividend, int divisor, int expected)
    {
        Assert.Equal(expected, _calculator.Divide(dividend, divisor));
    }
}
