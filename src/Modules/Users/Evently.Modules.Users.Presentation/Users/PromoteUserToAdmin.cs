using Evently.Common.Domain;
using Evently.Common.Presentation.Endpoints;
using Evently.Common.Presentation.Results;
using Evently.Modules.Users.Application.Users.PromoteToAdmin;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Evently.Modules.Users.Presentation.Users;

internal sealed class PromoteUserToAdmin : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        IHostEnvironment env = app.ServiceProvider.GetRequiredService<IHostEnvironment>();

        if (!env.IsDevelopment())
        {
            return;
        }

        app.MapPut("dev/users/{id:guid}/promote-admin", async (Guid id, ISender sender) =>
        {
            Result result = await sender.Send(new PromoteUserToAdminCommand(id));

            return result.Match(Results.NoContent, ApiResults.Problem);
        })
        .AllowAnonymous()
        .WithTags(Tags.Users);
    }
}
