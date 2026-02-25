namespace Evently.Simulator.Auth;

public sealed class VirtualUser
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    public string? AccessToken { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
