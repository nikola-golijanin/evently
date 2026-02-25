using Bogus;
using Evently.Simulator.Auth;
using Evently.Simulator.Clients;
using Evently.Simulator.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Bootstrap;

public sealed class SimulatorBootstrapper(
    SimulatorState state,
    SimulatorStateStore stateStore,
    TokenService tokenService,
    UsersClient usersClient,
    EventsClient eventsClient,
    IOptions<SimulatorOptions> options,
    ILogger<SimulatorBootstrapper> logger)
{
    private readonly SimulatorOptions _options = options.Value;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting simulator bootstrap...");

        await SetupAdminUsersAsync(cancellationToken);
        await LoadCategoriesAsync(cancellationToken);
        await LoadPublishedEventsAsync(cancellationToken);
        await LoadRegularUsersAsync(cancellationToken);

        logger.LogInformation(
            "Bootstrap complete. Admins: {AdminCount}, Categories: {CategoryCount}, Events: {EventCount}, Users: {UserCount}",
            state.AdminUsers.Count,
            state.CategoryIds.Count,
            state.PublishedEventIds.Count,
            state.RegularUsers.Count);
    }

    private async Task SetupAdminUsersAsync(CancellationToken cancellationToken)
    {
        foreach (AdminUserOptions adminConfig in _options.AdminUsers)
        {
            VirtualUser admin = new() { Email = adminConfig.Email, Password = adminConfig.Password };

            logger.LogInformation("Setting up admin {Email}...", adminConfig.Email);

            string? token = await tokenService.AcquireTokenAsync(admin, cancellationToken);

            if (token is not null)
            {
                // User already exists and is properly set up
                state.AdminUsers.Add(admin);
                logger.LogInformation("Admin {Email} already exists, logged in successfully", adminConfig.Email);
                continue;
            }

            // User doesn't exist — register, wait for propagation, then promote
            Guid? userId = await usersClient.RegisterUserAsync(
                adminConfig.Email,
                adminConfig.Password,
                adminConfig.FirstName,
                adminConfig.LastName,
                cancellationToken);

            if (userId is null)
            {
                logger.LogWarning("Failed to register admin {Email}, skipping", adminConfig.Email);
                continue;
            }

            logger.LogInformation(
                "Registered admin {Email} ({UserId}), waiting for integration events...",
                adminConfig.Email,
                userId);

            await Task.Delay(_options.AdminIntegrationEventPropagationDelayMs, cancellationToken);

            bool promoted = await usersClient.PromoteToAdminAsync(userId.Value, cancellationToken);

            if (!promoted)
            {
                logger.LogWarning("Failed to promote {Email} to admin, skipping", adminConfig.Email);
                continue;
            }

            token = await tokenService.AcquireTokenAsync(admin, cancellationToken);

            if (token is null)
            {
                logger.LogWarning("Failed to acquire token for new admin {Email}, skipping", adminConfig.Email);
                continue;
            }

            state.AdminUsers.Add(admin);
            logger.LogInformation("Admin {Email} set up successfully", adminConfig.Email);
        }

        if (state.AdminUsers.Count == 0)
        {
            logger.LogError("No admin users could be set up. Simulator may not function correctly.");
        }
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        if (state.AdminUsers.Count == 0)
        {
            return;
        }

        VirtualUser admin = state.AdminUsers[0];
        string? token = await tokenService.GetTokenAsync(admin, cancellationToken);

        if (token is null)
        {
            logger.LogWarning("No admin token available to load categories");
            return;
        }

        List<Guid> categoryIds = await eventsClient.GetCategoryIdsAsync(token, cancellationToken);

        foreach (Guid id in categoryIds)
        {
            state.CategoryIds.Add(id);
        }

        if (state.CategoryIds.IsEmpty)
        {
            logger.LogInformation("No categories found. Seeding initial categories...");
            await SeedCategoriesAsync(token, cancellationToken);
        }
    }

    private async Task SeedCategoriesAsync(string token, CancellationToken cancellationToken)
    {
        Faker faker = new();

        string[] categoryNames =
        [
            faker.Commerce.Department(),
            faker.Commerce.Department(),
            faker.Commerce.Department(),
            faker.Commerce.Department(),
            faker.Commerce.Department()
        ];

        foreach (string name in categoryNames)
        {
            Guid? categoryId = await eventsClient.CreateCategoryAsync(token, name, cancellationToken);

            if (categoryId is not null)
            {
                state.CategoryIds.Add(categoryId.Value);
                logger.LogInformation("Created category '{Name}' ({Id})", name, categoryId.Value);
            }
        }
    }

    private async Task LoadPublishedEventsAsync(CancellationToken cancellationToken)
    {
        if (state.AdminUsers.Count == 0)
        {
            return;
        }

        VirtualUser admin = state.AdminUsers[0];
        string? token = await tokenService.GetTokenAsync(admin, cancellationToken);

        if (token is null)
        {
            return;
        }

        List<Guid> eventIds = await eventsClient.GetEventIdsAsync(token, cancellationToken);

        foreach (Guid id in eventIds)
        {
            state.PublishedEventIds.Add(id);
        }

        logger.LogInformation("Loaded {Count} events from API", state.PublishedEventIds.Count);
    }

    private async Task LoadRegularUsersAsync(CancellationToken cancellationToken)
    {
        List<StoredUserCredentials> savedUsers = await stateStore.LoadRegularUsersAsync();

        foreach (StoredUserCredentials credentials in savedUsers)
        {
            VirtualUser user = new() { Email = credentials.Email, Password = credentials.Password };

            string? token = await tokenService.AcquireTokenAsync(user, cancellationToken);

            if (token is not null)
            {
                state.RegularUsers.Add(user);
            }
            else
            {
                logger.LogWarning("Saved user {Email} could not log in, skipping", credentials.Email);
            }
        }

        logger.LogInformation("Loaded {Count} regular users from state file", state.RegularUsers.Count);
    }
}
