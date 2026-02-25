using Bogus;
using Evently.Simulator.Auth;
using Evently.Simulator.Clients;
using Evently.Simulator.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Workers;

public sealed class AdminWorker(
    SimulatorState state,
    TokenService tokenService,
    EventsClient eventsClient,
    IOptions<SimulatorOptions> options,
    ILogger<AdminWorker> logger) : BackgroundService
{
    private readonly SimulatorOptions _options = options.Value;

    private static readonly string[] TicketTypeNames = ["General Admission", "VIP", "Early Bird", "Student"];
    private static readonly string[] Currencies = ["USD", "EUR", "GBP"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_options.AdminWorkerIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (state.AdminUsers.Count == 0)
        {
            logger.LogWarning("[AdminWorker] No admin users available, skipping cycle");
            return;
        }

        if (state.CategoryIds.IsEmpty)
        {
            logger.LogWarning("[AdminWorker] No categories available, skipping cycle");
            return;
        }

        VirtualUser admin = PickRandom(state.AdminUsers);
        string? token = await tokenService.GetTokenAsync(admin, cancellationToken);

        if (token is null)
        {
            logger.LogWarning("[AdminWorker] Could not acquire token for {Email}, skipping cycle", admin.Email);
            return;
        }

        Faker faker = new();

        Guid categoryId = PickRandom(state.CategoryIds.ToArray());

        string title = $"{faker.Commerce.ProductAdjective()} {faker.Commerce.ProductName()} Summit";
        string description = faker.Lorem.Paragraph();
        string location = $"{faker.Address.City()}, {faker.Address.Country()}";
        DateTime startsAt = DateTime.UtcNow.AddDays(faker.Random.Int(7, 60));
        DateTime endsAt = startsAt.AddDays(1);

        Guid? eventId = await eventsClient.CreateEventAsync(
            token,
            categoryId,
            title,
            description,
            location,
            startsAt,
            endsAt,
            cancellationToken);

        if (eventId is null)
        {
            return;
        }

        int ticketTypeCount = faker.Random.Int(1, 3);

        for (int i = 0; i < ticketTypeCount; i++)
        {
            string typeName = TicketTypeNames[faker.Random.Int(0, TicketTypeNames.Length - 1)];
            decimal price = faker.Random.Decimal(10, 250);
            string currency = Currencies[faker.Random.Int(0, Currencies.Length - 1)];
            decimal quantity = faker.Random.Decimal(50, 500);

            await eventsClient.CreateTicketTypeAsync(token, eventId.Value, typeName, price, currency, quantity, cancellationToken);
        }

        bool published = await eventsClient.PublishEventAsync(token, eventId.Value, cancellationToken);

        if (published)
        {
            state.PublishedEventIds.Add(eventId.Value);
            logger.LogInformation("[AdminWorker] Published event \"{Title}\" ({EventId})", title, eventId.Value);
        }
    }

    private static T PickRandom<T>(IList<T> list) => list[Random.Shared.Next(list.Count)];

    private static T PickRandom<T>(T[] array) => array[Random.Shared.Next(array.Length)];
}
