using Evently.Common.Application.Messaging;
using Evently.Modules.Ticketing.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Evently.Modules.Ticketing.Application.Watilists;

internal sealed class WaitlistEntryOfferedDomainEventHandler(
    ILogger<WaitlistEntryOfferedDomainEventHandler> logger)
    : DomainEventHandler<WaitlistEntryOfferedDomainEvent>
{
    public override Task Handle(
        WaitlistEntryOfferedDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        // Placeholder: will publish integration event for Notifications module (FR-001)
        logger.LogInformation(
            "Waitlist entry {WaitlistEntryId} offered to customer {CustomerId} for ticket type {TicketTypeId}",
            domainEvent.WaitlistEntryId,
            domainEvent.CustomerId,
            domainEvent.TicketTypeId);

        return Task.CompletedTask;
    }
}
