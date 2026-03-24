using Evently.Common.Domain;

namespace Evently.Modules.Ticketing.Domain.Events;

public sealed class WaitlistEntryOfferedDomainEvent(
    Guid waitlistEntryId,
    Guid customerId,
    Guid ticketTypeId) : DomainEvent
{
    public Guid WaitlistEntryId { get; init; } = waitlistEntryId;

    public Guid CustomerId { get; init; } = customerId;

    public Guid TicketTypeId { get; init; } = ticketTypeId;
}
