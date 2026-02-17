namespace Evently.Modules.Ticketing.Domain.Orders;

public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IEnumerable<Order>> GetForEventAsync(Guid eventId, CancellationToken cancellationToken = default);

    void Insert(Order order);
}
