using Evently.Common.Domain;

namespace Evently.Modules.Ticketing.Domain.Orders;

public static class OrderErrors
{
    public static Error NotFound(Guid orderId) =>
        Error.NotFound("Orders.NotFound", $"The order with the identifier {orderId} was not found");


    public static readonly Error TicketsAlreadyIssues = Error.Problem(
        "Order.TicketsAlreadyIssued",
        "The tickets for this order were already issued");

    public static readonly Error NotPaid = Error.Problem(
        "Orders.NotPaid",
        "The order cannot be refunded because it has not been paid");

    public static readonly Error AlreadyRefunded = Error.Problem(
        "Orders.AlreadyRefunded",
        "The order has already been refunded");

    public static readonly Error NotPending = Error.Problem(
        "Orders.NotPending",
        "The order cannot be canceled because it is not pending");
}
