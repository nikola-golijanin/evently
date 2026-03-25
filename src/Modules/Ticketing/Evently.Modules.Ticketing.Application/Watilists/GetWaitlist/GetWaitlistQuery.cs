using Evently.Common.Application.Messaging;

namespace Evently.Modules.Ticketing.Application.Watilists.GetWaitlist;

public sealed record GetWaitlistQuery(Guid WaitlistId) : IQuery<GetWaitlistResponse>;
