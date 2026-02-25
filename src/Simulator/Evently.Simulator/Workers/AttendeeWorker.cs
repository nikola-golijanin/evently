using Evently.Simulator.Auth;
using Evently.Simulator.Clients;
using Evently.Simulator.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Workers;

public sealed class AttendeeWorker(
    SimulatorState state,
    TokenService tokenService,
    TicketingClient ticketingClient,
    AttendanceClient attendanceClient,
    IOptions<SimulatorOptions> options,
    ILogger<AttendeeWorker> logger) : BackgroundService
{
    private readonly SimulatorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_options.AttendeeWorkerIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!state.PendingOrders.TryDequeue(out PendingOrder? pendingOrder))
        {
            return;
        }

        VirtualUser user = pendingOrder.User;
        string? token = await tokenService.GetTokenAsync(user, cancellationToken);

        if (token is null)
        {
            logger.LogWarning("[AttendeeWorker] Could not acquire token for {Email}, skipping check-in", user.Email);
            return;
        }

        List<TicketingClient.TicketDto> tickets = await ticketingClient.GetTicketsForOrderAsync(
            token,
            pendingOrder.OrderId,
            cancellationToken);

        if (tickets.Count == 0)
        {
            logger.LogWarning(
                "[AttendeeWorker] No tickets found for order {OrderId}, skipping check-in",
                pendingOrder.OrderId);

            return;
        }

        foreach (TicketingClient.TicketDto ticket in tickets)
        {
            bool checkedIn = await attendanceClient.CheckInAsync(token, ticket.Id, cancellationToken);

            if (checkedIn)
            {
                logger.LogInformation(
                    "[AttendeeWorker] Checked in ticket {TicketId} (code: {Code})",
                    ticket.Id,
                    ticket.Code);
            }
        }
    }
}
