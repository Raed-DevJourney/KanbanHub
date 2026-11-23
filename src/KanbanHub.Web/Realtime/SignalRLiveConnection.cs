using KanbanHub.Domain.Events;
using Microsoft.AspNetCore.SignalR.Client;

namespace KanbanHub.Web.Realtime;

/// <summary>The SignalR connection. It reconnects by itself a few times before it gives up and closes.</summary>
internal sealed class SignalRLiveConnection : ILiveConnection
{
    private readonly HubConnection hub;

    public SignalRLiveConnection(string url, Func<Task<string?>> accessToken)
    {
        hub = new HubConnectionBuilder()
            .WithUrl(url, options => options.AccessTokenProvider = accessToken)
            .WithAutomaticReconnect()
            .Build();

        hub.On<Guid, string, Guid>(IRealtimeGateway.BoardChangedMethod, (boardId, _, _) => BoardChanged?.Invoke(boardId));
        hub.On(IRealtimeGateway.InboxChangedMethod, () => InboxChanged?.Invoke());
        hub.Reconnecting += _ => RaiseAsync(LiveState.Reconnecting);
        hub.Reconnected += _ => RaiseAsync(LiveState.Connected);
        hub.Closed += _ => RaiseAsync(LiveState.Offline);
    }

    public event Func<LiveState, Task>? StateChanged;

    public event Action<Guid>? BoardChanged;

    public event Action? InboxChanged;

    public string? ConnectionId => hub.ConnectionId;

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await hub.StartAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever kept the hub away, the app works without it: the board just isn't live.
            return false;
        }
    }

    public ValueTask DisposeAsync() => hub.DisposeAsync();

    private Task RaiseAsync(LiveState state) => StateChanged?.Invoke(state) ?? Task.CompletedTask;
}

internal sealed class SignalRConnectionFactory : ILiveConnectionFactory
{
    public ILiveConnection Create(string url, Func<Task<string?>> accessToken) => new SignalRLiveConnection(url, accessToken);
}
