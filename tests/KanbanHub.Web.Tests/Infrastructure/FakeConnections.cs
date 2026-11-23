using KanbanHub.Web.Realtime;

namespace KanbanHub.Web.Tests.Infrastructure;

/// <summary>A real-time connection a test drives by hand: pushes, drops, comebacks.</summary>
internal sealed class FakeConnection(string url, Func<Task<string?>> accessToken, bool canReachHub) : ILiveConnection
{
    public event Func<LiveState, Task>? StateChanged;

    public event Action<Guid>? BoardChanged;

    public event Action? InboxChanged;

    public string Url => url;

    public Func<Task<string?>> AccessToken => accessToken;

    public string? ConnectionId { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if (canReachHub) ConnectionId = "connection-1";
        return Task.FromResult(canReachHub);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    public void PushBoardChange(Guid boardId) => BoardChanged?.Invoke(boardId);

    public void PushInboxChange() => InboxChanged?.Invoke();

    public Task DropAsync() => RaiseAsync(LiveState.Reconnecting);

    public Task ComeBackAsync(string newConnectionId)
    {
        ConnectionId = newConnectionId;
        return RaiseAsync(LiveState.Connected);
    }

    public Task CloseAsync() => RaiseAsync(LiveState.Offline);

    private Task RaiseAsync(LiveState state) => StateChanged?.Invoke(state) ?? Task.CompletedTask;
}

internal sealed class FakeConnections : ILiveConnectionFactory
{
    public List<FakeConnection> Created { get; } = [];

    public bool HubIsUp { get; set; } = true;

    public ILiveConnection Create(string url, Func<Task<string?>> accessToken)
    {
        var connection = new FakeConnection(url, accessToken, HubIsUp);
        Created.Add(connection);
        return connection;
    }
}
