namespace Shop;

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);

public sealed record Order(Guid Id, Guid CustomerId, IReadOnlyList<OrderLine> Lines, decimal Total, DateTimeOffset PlacedAt);

public interface IOrderRepository
{
    Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task SaveAsync(Order order, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public class OrderLimitExceededException(decimal total, decimal limit)
    : InvalidOperationException($"Order total {total} exceeds the limit of {limit}.")
{
    public decimal Total { get; } = total;

    public decimal Limit { get; } = limit;
}
