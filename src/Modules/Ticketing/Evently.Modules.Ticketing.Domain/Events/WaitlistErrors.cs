using Evently.Common.Domain;

namespace Evently.Modules.Ticketing.Domain.Events;

public static class WaitlistErrors
{
    public static Error InvalidStatus(Guid waitlistId, WaitlistEntryStatus status) =>
        Error.Problem(
            "Waitlist.InvalidStatus",
            $"The waitlist entry with the identifier {waitlistId} has invalid status {status}");

    public static Error TicketsStillAvailable(Guid ticketTypeId) =>
        Error.Problem(
            "Waitlist.TicketsStillAvailable",
            $"Cannot join waitlist for ticket type {ticketTypeId} because tickets are still available");

    public static Error AlreadyOnWaitlist(Guid ticketTypeId, Guid customerId) =>
        Error.Conflict(
            "Waitlist.AlreadyOnWaitlist",
            $"Customer {customerId} is already on the waitlist for ticket type {ticketTypeId}");

    public static readonly Error NotFound = Error.NotFound(
        "Waitlist.NotFound",
        "The waitlist entry was not found");
}
