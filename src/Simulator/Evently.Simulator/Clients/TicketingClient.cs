using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Clients;

public sealed class TicketingClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SimulatorOptions> options,
    ILogger<TicketingClient> logger)
{
    private readonly string _baseUrl = options.Value.TargetBaseUrl;

    public async Task<bool> AddToCartAsync(
        string token,
        Guid ticketTypeId,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PutAsJsonAsync(
                "carts/add",
                new { ticketTypeId, quantity },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to add ticket type {TicketTypeId} to cart: {StatusCode}",
                    ticketTypeId,
                    response.StatusCode);

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add ticket type {TicketTypeId} to cart", ticketTypeId);
            return false;
        }
    }

    public async Task<bool> CreateOrderAsync(string token, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "orders",
                new { },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to create order: {StatusCode}", response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create order");
            return false;
        }
    }

    public async Task<List<OrderDto>> GetOrdersAsync(string token, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            List<OrderDto>? orders = await client.GetFromJsonAsync<List<OrderDto>>("orders", cancellationToken);
            return orders ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get orders");
            return [];
        }
    }

    public async Task<List<TicketDto>> GetTicketsForOrderAsync(
        string token,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            List<TicketDto>? tickets = await client.GetFromJsonAsync<List<TicketDto>>(
                $"tickets/order/{orderId}",
                cancellationToken);

            return tickets ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get tickets for order {OrderId}", orderId);
            return [];
        }
    }

    private HttpClient CreateClient(string token)
    {
        HttpClient client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public sealed class OrderDto
    {
        public Guid Id { get; init; }

        public DateTime CreatedAtUtc { get; init; }
    }

    public sealed class TicketDto
    {
        public Guid Id { get; init; }

        public string Code { get; init; } = string.Empty;
    }
}
