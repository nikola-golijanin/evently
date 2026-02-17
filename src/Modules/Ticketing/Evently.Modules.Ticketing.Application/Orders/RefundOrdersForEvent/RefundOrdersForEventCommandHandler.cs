using System.Data.Common;
using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Ticketing.Application.Abstractions.Data;
using Evently.Modules.Ticketing.Domain.Orders;

namespace Evently.Modules.Ticketing.Application.Orders.RefundOrdersForEvent;

internal sealed class RefundOrdersForEventCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RefundOrdersForEventCommand>
{
    public async Task<Result> Handle(RefundOrdersForEventCommand request, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        IEnumerable<Order> orders = await orderRepository.GetForEventAsync(request.EventId, cancellationToken);

        foreach (Order order in orders)
        {
            order.Refund();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }
}
