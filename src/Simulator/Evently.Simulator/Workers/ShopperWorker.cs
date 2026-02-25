using Bogus;
using Evently.Simulator.Auth;
using Evently.Simulator.Clients;
using Evently.Simulator.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Workers;

public sealed class ShopperWorker(
    SimulatorState state,
    SimulatorStateStore stateStore,
    TokenService tokenService,
    UsersClient usersClient,
    EventsClient eventsClient,
    TicketingClient ticketingClient,
    IOptions<SimulatorOptions> options,
    ILogger<ShopperWorker> logger) : BackgroundService
{
    private readonly SimulatorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_options.ShopperWorkerIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (Random.Shared.NextDouble() < _options.NewUserRegistrationChance)
        {
            await RegisterNewUserAsync(cancellationToken);
        }

        VirtualUser[] regularUsers = state.RegularUsers.ToArray();

        if (regularUsers.Length == 0)
        {
            logger.LogWarning("[ShopperWorker] No regular users available, skipping purchase cycle");
            return;
        }

        if (state.PublishedEventIds.IsEmpty)
        {
            logger.LogWarning("[ShopperWorker] No published events available, skipping purchase cycle");
            return;
        }

        VirtualUser user = regularUsers[Random.Shared.Next(regularUsers.Length)];
        string? token = await tokenService.GetTokenAsync(user, cancellationToken);

        if (token is null)
        {
            logger.LogWarning("[ShopperWorker] Could not acquire token for {Email}, skipping", user.Email);
            return;
        }

        Guid[] eventIds = state.PublishedEventIds.ToArray();
        Guid eventId = eventIds[Random.Shared.Next(eventIds.Length)];

        List<EventsClient.TicketTypeDto> ticketTypes = await eventsClient.GetTicketTypesAsync(token, eventId, cancellationToken);

        if (ticketTypes.Count == 0)
        {
            logger.LogWarning("[ShopperWorker] No ticket types for event {EventId}, skipping", eventId);
            return;
        }

        EventsClient.TicketTypeDto ticketType = ticketTypes[Random.Shared.Next(ticketTypes.Count)];

        bool added = await ticketingClient.AddToCartAsync(token, ticketType.Id, 1, cancellationToken);

        if (!added)
        {
            return;
        }

        bool ordered = await ticketingClient.CreateOrderAsync(token, cancellationToken);

        if (!ordered)
        {
            return;
        }

        List<TicketingClient.OrderDto> orders = await ticketingClient.GetOrdersAsync(token, cancellationToken);

        if (orders.Count == 0)
        {
            return;
        }

        TicketingClient.OrderDto latestOrder = orders.MaxBy(o => o.CreatedAtUtc)!;

        state.PendingOrders.Enqueue(new PendingOrder(user, latestOrder.Id));

        logger.LogInformation(
            "[ShopperWorker] User {Email} placed order {OrderId} for event {EventId}",
            user.Email,
            latestOrder.Id,
            eventId);
    }

    private async Task RegisterNewUserAsync(CancellationToken cancellationToken)
    {
        Faker faker = new();

        string firstName = faker.Name.FirstName();
        string lastName = faker.Name.LastName();
        string email = faker.Internet.Email(firstName, lastName);
        string password = $"User{faker.Random.Int(1000, 9999)}!";

        Guid? userId = await usersClient.RegisterUserAsync(email, password, firstName, lastName, cancellationToken);

        if (userId is null)
        {
            return;
        }

        VirtualUser user = new() { Email = email, Password = password };
        string? token = await tokenService.AcquireTokenAsync(user, cancellationToken);

        if (token is null)
        {
            logger.LogWarning("[ShopperWorker] Registered {Email} but could not log in", email);
            return;
        }

        state.RegularUsers.Add(user);

        await stateStore.SaveRegularUsersAsync(state.RegularUsers);

        logger.LogInformation("[ShopperWorker] Registered new user {Email}", email);
    }
}
