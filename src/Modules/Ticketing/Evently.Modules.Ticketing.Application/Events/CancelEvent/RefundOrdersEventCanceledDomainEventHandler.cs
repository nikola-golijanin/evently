using Evently.Common.Application.Exceptions;
using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Ticketing.Application.Orders.RefundOrdersForEvent;
using Evently.Modules.Ticketing.Domain.Events;
using MediatR;

namespace Evently.Modules.Ticketing.Application.Events.CancelEvent;

internal sealed class RefundOrdersEventCanceledDomainEventHandler(ISender sender)
    : DomainEventHandler<EventCanceledDomainEvent>
{
    public override async Task Handle(
        EventCanceledDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        Result result = await sender.Send(new RefundOrdersForEventCommand(domainEvent.EventId), cancellationToken);

        if (result.IsFailure)
        {
            throw new EventlyException(nameof(RefundOrdersForEventCommand), result.Error);
        }
    }
}
