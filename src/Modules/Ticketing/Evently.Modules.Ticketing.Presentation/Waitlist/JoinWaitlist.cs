using Evently.Common.Domain;
using Evently.Common.Presentation.Endpoints;
using Evently.Common.Presentation.Results;
using Evently.Modules.Ticketing.Application.Abstractions.Authentication;
using Evently.Modules.Ticketing.Application.Watilists.JoinWaitlist;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evently.Modules.Ticketing.Presentation.Waitlist;

public class JoinWaitlist : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("waitlist/join", async (ICustomerContext customerContext, ISender sender, JoinWaitlistRequest request) =>
        {
            Result result = await sender.Send(
                new JoinWaitlistCommand(request.EventId, request.TicketTypeId, customerContext.CustomerId, request.Quantity));

            return result.Match(Results.NoContent, ApiResults.Problem);
        })
        .RequireAuthorization(Permissions.JoinWaitlist)
        .WithTags(Tags.Waitlist);
    }

    internal sealed class JoinWaitlistRequest
    {
        public Guid EventId { get; init; }

        public Guid TicketTypeId { get; init; }

        public int Quantity { get; init; }
    }
}
