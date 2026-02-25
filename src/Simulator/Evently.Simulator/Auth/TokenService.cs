using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evently.Simulator.Auth;

public sealed class TokenService(
    IHttpClientFactory httpClientFactory,
    IOptions<SimulatorOptions> options,
    ILogger<TokenService> logger)
{
    private readonly SimulatorOptions _options = options.Value;

    public async Task<string?> GetTokenAsync(VirtualUser user, CancellationToken cancellationToken = default)
    {
        if (user.AccessToken is not null && user.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5))
        {
            return user.AccessToken;
        }

        return await AcquireTokenAsync(user, cancellationToken);
    }

    public async Task<string?> AcquireTokenAsync(VirtualUser user, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpClient client = httpClientFactory.CreateClient();

            Dictionary<string, string> formData = new()
            {
                ["grant_type"] = "password",
                ["client_id"] = _options.PublicClientId,
                ["username"] = user.Email,
                ["password"] = user.Password,
                ["scope"] = "openid"
            };

            using FormUrlEncodedContent formContent = new(formData);
            using HttpResponseMessage response = await client.PostAsync(
                _options.KeycloakTokenUrl,
                formContent,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Token acquisition failed for {Email}: {StatusCode}",
                    user.Email,
                    response.StatusCode);

                return null;
            }

            TokenResponse? tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

            if (tokenResponse is null)
            {
                return null;
            }

            user.AccessToken = tokenResponse.AccessToken;
            user.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            return user.AccessToken;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception acquiring token for {Email}", user.Email);
            return null;
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
