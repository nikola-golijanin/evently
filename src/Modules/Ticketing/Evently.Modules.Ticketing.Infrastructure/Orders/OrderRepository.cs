using Evently.Modules.Ticketing.Domain.Orders;
using Evently.Modules.Ticketing.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Evently.Modules.Ticketing.Infrastructure.Orders;

internal sealed class OrderRepository(TicketingDbContext context) : IOrderRepository
{
    public async Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Orders
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<Order>> GetForEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return await (
            from order in context.Orders
            join orderItem in context.OrderItems on order.Id equals orderItem.OrderId
            join ticketType in context.TicketTypes on orderItem.TicketTypeId equals ticketType.Id
            where ticketType.EventId == eventId
            select order)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public void Insert(Order order)
    {
        context.Orders.Add(order);
    }
}
