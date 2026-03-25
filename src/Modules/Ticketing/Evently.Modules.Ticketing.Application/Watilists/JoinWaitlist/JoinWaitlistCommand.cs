using Evently.Common.Application.Messaging;

namespace Evently.Modules.Ticketing.Application.Watilists.JoinWaitlist;

public sealed record JoinWaitlistCommand(Guid EventId, Guid TicketTypeId, Guid CustomerId, int Quantity) : ICommand;
