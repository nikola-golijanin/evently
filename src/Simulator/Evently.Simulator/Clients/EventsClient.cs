using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Clients;

public sealed class EventsClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SimulatorOptions> options,
    ILogger<EventsClient> logger)
{
    private readonly string _baseUrl = options.Value.TargetBaseUrl;

    public async Task<List<Guid>> GetCategoryIdsAsync(string token, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            List<CategoryDto>? categories = await client.GetFromJsonAsync<List<CategoryDto>>(
                "categories",
                cancellationToken);

            return categories?.ConvertAll(c => c.Id) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get categories");
            return [];
        }
    }

    public async Task<Guid?> CreateCategoryAsync(string token, string name, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "categories",
                new { name },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to create category '{Name}': {StatusCode}", name, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<Guid>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create category '{Name}'", name);
            return null;
        }
    }

    public async Task<List<Guid>> GetEventIdsAsync(string token, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            List<EventDto>? events = await client.GetFromJsonAsync<List<EventDto>>("events", cancellationToken);

            return events?.ConvertAll(e => e.Id) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get events");
            return [];
        }
    }

    public async Task<Guid?> CreateEventAsync(
        string token,
        Guid categoryId,
        string title,
        string description,
        string location,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "events",
                new
                {
                    categoryId,
                    title,
                    description,
                    location,
                    startsAtUtc,
                    endsAtUtc
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to create event '{Title}': {StatusCode}", title, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<Guid>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create event '{Title}'", title);
            return null;
        }
    }

    public async Task<Guid?> CreateTicketTypeAsync(
        string token,
        Guid eventId,
        string name,
        decimal price,
        string currency,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "ticket-types",
                new { eventId, name, price, currency, quantity },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to create ticket type '{Name}': {StatusCode}", name, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<Guid>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create ticket type '{Name}'", name);
            return null;
        }
    }

    public async Task<bool> PublishEventAsync(string token, Guid eventId, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            using HttpResponseMessage response = await client.PutAsync(
                $"events/{eventId}/publish",
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to publish event {EventId}: {StatusCode}", eventId, response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish event {EventId}", eventId);
            return false;
        }
    }

    public async Task<List<TicketTypeDto>> GetTicketTypesAsync(
        string token,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient(token);

        try
        {
            List<TicketTypeDto>? ticketTypes = await client.GetFromJsonAsync<List<TicketTypeDto>>(
                $"ticket-types?eventId={eventId}",
                cancellationToken);

            return ticketTypes ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get ticket types for event {EventId}", eventId);
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

    public sealed class CategoryDto
    {
        public Guid Id { get; init; }
    }

    public sealed class EventDto
    {
        public Guid Id { get; init; }
    }

    public sealed class TicketTypeDto
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public decimal Price { get; init; }

        public decimal Quantity { get; init; }
    }
}
