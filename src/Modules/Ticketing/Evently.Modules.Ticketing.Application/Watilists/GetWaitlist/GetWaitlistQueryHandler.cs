using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Ticketing.Domain.Events;

namespace Evently.Modules.Ticketing.Application.Watilists.GetWaitlist;

internal sealed class GetWaitlistQueryHandler(IWaitlistEntryRepository waitlistEntryRepository) : IQueryHandler<GetWaitlistQuery, GetWaitlistResponse>
{
    public async Task<Result<GetWaitlistResponse>> Handle(GetWaitlistQuery request, CancellationToken cancellationToken)
    {
        WaitlistEntry? waitlistEntry = await waitlistEntryRepository.GetAsync(request.WaitlistId, cancellationToken);
        if (waitlistEntry is null)
        {
            return Result.Failure<GetWaitlistResponse>(WaitlistErrors.NotFound);
        }

        var response = new GetWaitlistResponse(waitlistEntry.EventId, waitlistEntry.TicketTypeId);
        return Result.Success(response);
    }
}
