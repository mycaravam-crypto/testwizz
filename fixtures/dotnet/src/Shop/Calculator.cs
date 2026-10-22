namespace Shop;

public class Calculator
{
    public int Add(int a, int b) => a + b;

    /// <summary>Divides <paramref name="dividend"/> by <paramref name="divisor"/>.</summary>
    /// <exception cref="DivideByZeroException">When <paramref name="divisor"/> is zero.</exception>
    public int Divide(int dividend, int divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException("Divisor must not be zero.");
        }

        return dividend / divisor;
    }

    public decimal Percentage(decimal value, decimal percent)
    {
        if (percent < 0 || percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent must be between 0 and 100.");
        }

        return value * percent / 100m;
    }

    public decimal Percentage(decimal value, int percent) => Percentage(value, (decimal)percent);
}
