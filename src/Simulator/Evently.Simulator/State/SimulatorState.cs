using System.Collections.Concurrent;
using Evently.Simulator.Auth;

namespace Evently.Simulator.State;

public sealed class SimulatorState
{
    public List<VirtualUser> AdminUsers { get; } = [];

    public ConcurrentBag<VirtualUser> RegularUsers { get; } = [];

    public ConcurrentBag<Guid> CategoryIds { get; } = [];

    public ConcurrentBag<Guid> PublishedEventIds { get; } = [];

    public ConcurrentQueue<PendingOrder> PendingOrders { get; } = new();
}

public sealed record PendingOrder(VirtualUser User, Guid OrderId);
