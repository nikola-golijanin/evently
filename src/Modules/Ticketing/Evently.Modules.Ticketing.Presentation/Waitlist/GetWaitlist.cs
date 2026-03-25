using Evently.Common.Domain;
using Evently.Common.Presentation.Endpoints;
using Evently.Common.Presentation.Results;
using Evently.Modules.Ticketing.Application.Watilists.GetWaitlist;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evently.Modules.Ticketing.Presentation.Waitlist;

public class GetWaitlist : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("waitlist/{id}", async (Guid id, ISender sender) =>
        {
            Result<GetWaitlistResponse> result = await sender.Send(new GetWaitlistQuery(id));

            return result.Match(Results.Ok, ApiResults.Problem);
        })
        .RequireAuthorization(Permissions.ViewWaitlist)
        .WithTags(Tags.Waitlist);
    }
}
