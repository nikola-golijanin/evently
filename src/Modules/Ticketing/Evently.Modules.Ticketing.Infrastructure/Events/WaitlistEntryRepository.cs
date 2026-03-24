using Evently.Modules.Ticketing.Domain.Events;
using Evently.Modules.Ticketing.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Evently.Modules.Ticketing.Infrastructure.Events;

internal sealed class WaitlistEntryRepository(TicketingDbContext context) : IWaitlistEntryRepository
{
    public async Task<WaitlistEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.WaitlistEntries.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        Guid customerId,
        Guid ticketTypeId,
        CancellationToken cancellationToken = default)
    {
        return await context.WaitlistEntries
            .AnyAsync(
                w => w.CustomerId == customerId
                     && w.TicketTypeId == ticketTypeId
                     && (w.Status == WaitlistEntryStatus.Waiting || w.Status == WaitlistEntryStatus.Offered),
                cancellationToken);
    }

    public async Task<WaitlistEntry?> GetNextWaitingAsync(
        Guid ticketTypeId,
        CancellationToken cancellationToken = default)
    {
        return await context.WaitlistEntries
            .Where(w => w.TicketTypeId == ticketTypeId && w.Status == WaitlistEntryStatus.Waiting)
            .OrderBy(w => w.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public void Insert(WaitlistEntry entry)
    {
        context.WaitlistEntries.Add(entry);
    }
}
