using Evently.Common.Domain;

namespace Evently.Modules.Ticketing.Domain.Events;

public sealed class WaitlistEntry : Entity
{
    private WaitlistEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid TicketTypeId { get; private set; }

    public Guid CustomerId { get; private set; }

    public int Quantity { get; private set; }

    public WaitlistEntryStatus Status { get; private set; }

    public DateTime RequestedAtUtc { get; private set; }

    public DateTime? OfferedAtUtc { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public static WaitlistEntry Create(Guid eventId, Guid ticketTypeId, Guid customerId, int quantity)
    {
        var entry = new WaitlistEntry
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            TicketTypeId = ticketTypeId,
            CustomerId = customerId,
            Quantity = quantity,
            Status = WaitlistEntryStatus.Waiting,
            RequestedAtUtc = DateTime.UtcNow
        };

        return entry;
    }

    public Result Offer(DateTime expiresAtUtc)
    {
        if (Status != WaitlistEntryStatus.Waiting)
        {
            return Result.Failure(WaitlistErrors.InvalidStatus(Id, Status));
        }

        Status = WaitlistEntryStatus.Offered;
        OfferedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = expiresAtUtc;

        Raise(new WaitlistEntryOfferedDomainEvent(Id, CustomerId, TicketTypeId));

        return Result.Success();
    }

    public Result Convert()
    {
        if (Status != WaitlistEntryStatus.Offered)
        {
            return Result.Failure(WaitlistErrors.InvalidStatus(Id, Status));
        }

        Status = WaitlistEntryStatus.Converted;

        return Result.Success();
    }

    public Result Expire()
    {
        if (Status != WaitlistEntryStatus.Offered)
        {
            return Result.Failure(WaitlistErrors.InvalidStatus(Id, Status));
        }

        Status = WaitlistEntryStatus.Expired;

        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status is WaitlistEntryStatus.Converted or WaitlistEntryStatus.Expired)
        {
            return Result.Failure(WaitlistErrors.InvalidStatus(Id, Status));
        }

        Status = WaitlistEntryStatus.Canceled;

        return Result.Success();
    }
}
