using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Users.Application.Abstractions.Data;
using Evently.Modules.Users.Domain.Users;

namespace Evently.Modules.Users.Application.Users.PromoteToAdmin;

internal sealed class PromoteUserToAdminCommandHandler(IUserRepository userRepository, IUnitOfWork unitOfWork)
    : ICommandHandler<PromoteUserToAdminCommand>
{
    public async Task<Result> Handle(PromoteUserToAdminCommand request, CancellationToken cancellationToken)
    {
        User? user = await userRepository.GetAsync(request.UserId, cancellationToken);

        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound(request.UserId));
        }

        user.PromoteToAdmin();

        userRepository.Update(user);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
