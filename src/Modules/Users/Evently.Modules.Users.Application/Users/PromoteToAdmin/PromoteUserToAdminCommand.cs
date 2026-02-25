using Evently.Common.Application.Messaging;

namespace Evently.Modules.Users.Application.Users.PromoteToAdmin;

public sealed record PromoteUserToAdminCommand(Guid UserId) : ICommand;
