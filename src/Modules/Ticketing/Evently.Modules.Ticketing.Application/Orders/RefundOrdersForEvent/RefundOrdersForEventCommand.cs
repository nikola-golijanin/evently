using Evently.Common.Application.Messaging;

namespace Evently.Modules.Ticketing.Application.Orders.RefundOrdersForEvent;

public sealed record RefundOrdersForEventCommand(Guid EventId) : ICommand;
