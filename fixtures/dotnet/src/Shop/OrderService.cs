namespace Shop;

public class OrderService
{
    public const decimal MaxOrderTotal = 10_000m;

    private readonly IOrderRepository _repository;
    private readonly IClock _clock;

    public OrderService(IOrderRepository repository, IClock clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Validates and stores a new order.</summary>
    public async Task<Order> PlaceOrderAsync(Guid customerId, IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(lines));
        }

        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                throw new ArgumentException($"Quantity for {line.Sku} must be positive.", nameof(lines));
            }
        }

        var total = lines.Sum(l => l.Quantity * l.UnitPrice);
        if (total > MaxOrderTotal)
        {
            throw new OrderLimitExceededException(total, MaxOrderTotal);
        }

        var order = new Order(Guid.NewGuid(), customerId, lines, total, _clock.UtcNow);
        await _repository.SaveAsync(order, cancellationToken);
        return order;
    }

    public async Task<bool> CancelAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await _repository.FindAsync(orderId, cancellationToken);
        if (order is null)
        {
            return false;
        }

        if (_clock.UtcNow - order.PlacedAt > TimeSpan.FromHours(24))
        {
            throw new InvalidOperationException("Orders can only be cancelled within 24 hours.");
        }

        return true;
    }
}
