using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Clients;

public sealed class UsersClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SimulatorOptions> options,
    ILogger<UsersClient> logger)
{
    private readonly string _baseUrl = options.Value.TargetBaseUrl;

    public async Task<Guid?> RegisterUserAsync(
        string email,
        string password,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_baseUrl);

        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "users/register",
                new { email, password, firstName, lastName },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to register user {Email}: {StatusCode}", email, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<Guid>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to register user {Email}", email);
            return null;
        }
    }

    public async Task<bool> PromoteToAdminAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_baseUrl);

        try
        {
            using HttpResponseMessage response = await client.PutAsync(
                $"dev/users/{userId}/promote-admin",
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to promote user {UserId} to admin: {StatusCode}", userId, response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to promote user {UserId} to admin", userId);
            return false;
        }
    }
}
