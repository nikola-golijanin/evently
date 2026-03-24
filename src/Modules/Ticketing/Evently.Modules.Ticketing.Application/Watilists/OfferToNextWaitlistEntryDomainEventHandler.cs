using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Ticketing.Application.Abstractions.Data;
using Evently.Modules.Ticketing.Domain.Events;
using Evently.Modules.Ticketing.Domain.Tickets;
using Microsoft.Extensions.Logging;

namespace Evently.Modules.Ticketing.Application.Watilists;

internal sealed class OfferToNextWaitlistEntryDomainEventHandler(
    ITicketRepository ticketRepository,
    IWaitlistEntryRepository waitlistEntryRepository,
    IUnitOfWork unitOfWork,
    ILogger<OfferToNextWaitlistEntryDomainEventHandler> logger)
    : DomainEventHandler<TicketArchivedDomainEvent>
{
    public override async Task Handle(
        TicketArchivedDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        Ticket? ticket = await ticketRepository.GetAsync(domainEvent.TicketId, cancellationToken);

        if (ticket is null)
        {
            logger.LogWarning("Ticket {TicketId} not found for waitlist offer", domainEvent.TicketId);
            return;
        }

        WaitlistEntry? nextEntry = await waitlistEntryRepository.GetNextWaitingAsync(
            ticket.TicketTypeId, cancellationToken);

        if (nextEntry is null)
        {
            logger.LogInformation(
                "No waitlist entries for ticket type {TicketTypeId}",
                ticket.TicketTypeId);
            return;
        }

        Result result = nextEntry.Offer(DateTime.UtcNow.AddMinutes(30));

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Failed to offer waitlist entry {WaitlistEntryId}: {Error}",
                nextEntry.Id,
                result.Error.Description);
            return;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
