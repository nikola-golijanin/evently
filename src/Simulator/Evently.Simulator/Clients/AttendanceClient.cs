using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Clients;

public sealed class AttendanceClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SimulatorOptions> options,
    ILogger<AttendanceClient> logger)
{
    private readonly string _baseUrl = options.Value.TargetBaseUrl;

    public async Task<bool> CheckInAsync(
        string token,
        Guid ticketId,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PutAsJsonAsync(
                "attendees/check-in",
                new { ticketId },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to check in ticket {TicketId}: {StatusCode}",
                    ticketId,
                    response.StatusCode);

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check in ticket {TicketId}", ticketId);
            return false;
        }
    }

    private HttpClient CreateClient(string token)
    {
        HttpClient client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
