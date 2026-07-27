using Microsoft.AspNetCore.SignalR;

namespace Envoy;

public sealed class ChatHub : Hub
{
    private readonly PresenceService _presence;
    public ChatHub(PresenceService presence) => _presence = presence;

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("presence", _presence.Count);
        await base.OnConnectedAsync();
    }

    public Task Identify(string name)
    {
        Context.Items["name"] = string.IsNullOrWhiteSpace(name) ? "访客" : name.Trim()[..Math.Min(name.Trim().Length, 40)];
        return Task.CompletedTask;
    }
}

public sealed class PresenceService
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public int Join() => Interlocked.Increment(ref _count);
    public int Leave() => Math.Max(0, Interlocked.Decrement(ref _count));
}
