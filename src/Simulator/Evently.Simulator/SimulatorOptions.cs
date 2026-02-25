namespace Evently.Simulator;

public sealed class SimulatorOptions
{
    public string TargetBaseUrl { get; init; } = "http://localhost:5000";

    public string KeycloakTokenUrl { get; init; } = "http://localhost:18080/realms/evently/protocol/openid-connect/token";

    public string PublicClientId { get; init; } = "evently-public-client";

    public int AdminWorkerIntervalSeconds { get; init; } = 60;

    public int ShopperWorkerIntervalSeconds { get; init; } = 30;

    public int AttendeeWorkerIntervalSeconds { get; init; } = 45;

    public double NewUserRegistrationChance { get; init; } = 0.25;

    public int AdminIntegrationEventPropagationDelayMs { get; init; } = 2000;

    public List<AdminUserOptions> AdminUsers { get; init; } = [];
}

public sealed class AdminUserOptions
{
    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;
}
