using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Ticketing.Application.Abstractions.Data;
using Evently.Modules.Ticketing.Domain.Events;

namespace Evently.Modules.Ticketing.Application.Watilists.JoinWaitlist;

internal sealed class JoinWaitlistCommandHandler(
    ITicketTypeRepository ticketTypeRepository,
    IWaitlistEntryRepository waitlistEntryRepository,
    IUnitOfWork unitOfWork)
     : ICommandHandler<JoinWaitlistCommand>
{
    public async Task<Result> Handle(JoinWaitlistCommand request, CancellationToken cancellationToken)
    {
        TicketType? ticketType = await ticketTypeRepository.GetAsync(request.TicketTypeId, cancellationToken);

        if (ticketType is null)
        {
            return Result.Failure(TicketTypeErrors.NotFound(request.TicketTypeId));
        }

        if (ticketType.EventId != request.EventId)
        {
            return Result.Failure(TicketTypeErrors.InvalidEvent(request.TicketTypeId, request.EventId));
        }

        if (ticketType.AvailableQuantity > 0)
        {
            return Result.Failure(WaitlistErrors.TicketsStillAvailable(request.TicketTypeId));
        }

        if (await waitlistEntryRepository.ExistsAsync(request.CustomerId, request.TicketTypeId, cancellationToken))
        {
            return Result.Failure(WaitlistErrors.AlreadyOnWaitlist(request.TicketTypeId, request.CustomerId));
        }

        var entry = WaitlistEntry.Create(
            request.EventId, request.TicketTypeId, request.CustomerId, request.Quantity);

        waitlistEntryRepository.Insert(entry);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
