using System.Text.Json;
using Evently.Simulator.Auth;
using Microsoft.Extensions.Logging;

namespace Evently.Simulator.State;

public sealed class SimulatorStateStore(ILogger<SimulatorStateStore> logger)
{
    private const string StateFilePath = "simulator-state.json";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public async Task<List<StoredUserCredentials>> LoadRegularUsersAsync()
    {
        if (!File.Exists(StateFilePath))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(StateFilePath);
            StateFile? state = await JsonSerializer.DeserializeAsync<StateFile>(stream);
            return state?.RegularUsers ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load simulator state from {Path}", StateFilePath);
            return [];
        }
    }

    public async Task SaveRegularUsersAsync(IEnumerable<VirtualUser> users)
    {
        StateFile state = new()
        {
            RegularUsers = users
                .Select(u => new StoredUserCredentials(u.Email, u.Password))
                .ToList()
        };

        await using FileStream stream = File.Create(StateFilePath);

        await JsonSerializer.SerializeAsync(stream, state, IndentedOptions);
    }

    private sealed class StateFile
    {
        public List<StoredUserCredentials> RegularUsers { get; init; } = [];
    }
}

public sealed record StoredUserCredentials(string Email, string Password);
