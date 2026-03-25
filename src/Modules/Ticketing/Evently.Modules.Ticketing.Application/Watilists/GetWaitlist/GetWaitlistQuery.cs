using Evently.Common.Application.Messaging;

namespace Evently.Modules.Ticketing.Application.Watilists.GetWaitlist;

public record GetWaitlistQuery(Guid WaitlistId) : IQuery<GetWaitlistResponse>;
