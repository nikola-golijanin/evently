using FluentValidation;

namespace Evently.Modules.Ticketing.Application.Orders.RefundOrdersForEvent;

internal sealed class RefundOrdersForEventCommandValidator : AbstractValidator<RefundOrdersForEventCommand>
{
    public RefundOrdersForEventCommandValidator()
    {
        RuleFor(c => c.EventId).NotEmpty();
    }
}
