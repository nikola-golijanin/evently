using Evently.Common.Application.Messaging;

namespace Evently.Modules.Ticketing.Application.Watilists.JoinWaitlist;

public record JoinWaitlistCommand(Guid EventId, Guid TicketTypeId, Guid CustomerId, int Quantity) : ICommand;
