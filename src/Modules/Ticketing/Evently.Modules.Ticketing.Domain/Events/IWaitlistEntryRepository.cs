namespace Evently.Modules.Ticketing.Domain.Events;

public interface IWaitlistEntryRepository
{
    Task<WaitlistEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        Guid customerId,
        Guid ticketTypeId,
        CancellationToken cancellationToken = default);

    Task<WaitlistEntry?> GetNextWaitingAsync(Guid ticketTypeId, CancellationToken cancellationToken = default);

    void Insert(WaitlistEntry entry);
}
