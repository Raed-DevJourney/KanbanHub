using KanbanHub.Domain.Events;
using KanbanHub.Web.Api;

namespace KanbanHub.Web.Realtime;

public enum LiveState
{
    Offline,
    Connected,
    Reconnecting
}

/// <summary>A connection to the real-time hub, behind an interface so the rules around it can be tested without one.</summary>
public interface ILiveConnection : IAsyncDisposable
{
    /// <summary>Raised when the connection drops, comes back or closes for good.</summary>
    event Func<LiveState, Task>? StateChanged;

    event Action<Guid>? BoardChanged;

    event Action? InboxChanged;

    /// <summary>The id the server knows this connection by. Every reconnect gets a new one.</summary>
    string? ConnectionId { get; }

    /// <summary>False when the hub can't be reached.</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken);
}

public interface ILiveConnectionFactory
{
    /// <param name="url">Where the hub is.</param>
    /// <param name="accessToken">Asked on every connect and reconnect, so the token is never a stale one.</param>
    ILiveConnection Create(string url, Func<Task<string?>> accessToken);
}

/// <summary>
/// Keeps the screen live (FR-M06): says when someone changed the board on screen and when the inbox got mail.
/// A push is a hint, not data: listeners refetch, so a missed or repeated push does no harm.
/// </summary>
public sealed class LiveUpdates(KanbanApi api, ILiveConnectionFactory connections, TimeProvider clock) : IAsyncDisposable
{
    /// <summary>One connect asks for its token more than once; a token this young is as good as a new one.</summary>
    private static readonly TimeSpan TokenIsFreshFor = TimeSpan.FromMinutes(1);

    private RealtimeConnection? hub;
    private DateTimeOffset negotiatedAt;
    private ILiveConnection? connection;
    private Task? opening;
    private Guid? watchedBoardId;

    public event Action? StateChanged;

    /// <summary>The watched board changed, or the connection was away and may have missed that it did.</summary>
    public event Action? BoardChanged;

    public event Action? InboxChanged;

    public LiveState State { get; private set; } = LiveState.Offline;

    /// <summary>Opens the connection once, however many parts of the page ask for it.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken) => opening ??= OpenAsync(cancellationToken);

    /// <summary>
    /// Follows one board (a board followed before is no longer listened to) and reports it as changed:
    /// whatever happened to it before this moment was not pushed here.
    /// </summary>
    public async Task WatchAsync(Guid boardId, CancellationToken cancellationToken)
    {
        watchedBoardId = boardId;
        await ConnectAsync(cancellationToken);
        await JoinWatchedBoardAsync(cancellationToken);
        BoardChanged?.Invoke();
    }

    /// <summary>Starts over after the connection closed for good, and fetches what was missed meanwhile.</summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await CloseAsync();
        await ConnectAsync(cancellationToken);
        if (State == LiveState.Connected) await CatchUpAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (await NegotiateAsync(cancellationToken) is not { } address) return;

        connection = connections.Create(address.Url, FreshTokenAsync);
        connection.BoardChanged += OnBoardChanged;
        connection.InboxChanged += OnInboxChanged;
        connection.StateChanged += OnStateChangedAsync;
        Set(await connection.StartAsync(cancellationToken) ? LiveState.Connected : LiveState.Offline);
    }

    private async Task CloseAsync()
    {
        if (connection is { } open) await open.DisposeAsync();

        connection = null;
        opening = null;
        Set(LiveState.Offline);
    }

    private async Task<string?> FreshTokenAsync() => (await NegotiateAsync(CancellationToken.None))?.AccessToken;

    /// <summary>Where the hub is and a token for it, asked from the API unless the last answer is still fresh.</summary>
    private async Task<RealtimeConnection?> NegotiateAsync(CancellationToken cancellationToken)
    {
        if (hub is not null && clock.GetUtcNow() - negotiatedAt < TokenIsFreshFor) return hub;

        hub = (await api.NegotiateRealtimeAsync(cancellationToken)).Value as RealtimeConnection;
        negotiatedAt = clock.GetUtcNow();
        return hub;
    }

    private async Task JoinWatchedBoardAsync(CancellationToken cancellationToken)
    {
        if (connection?.ConnectionId is { } connectionId && watchedBoardId is { } boardId)
            await api.JoinBoardAsync(boardId, connectionId, cancellationToken);
    }

    private async Task OnStateChangedAsync(LiveState state)
    {
        Set(state);
        if (state == LiveState.Connected) await CatchUpAsync(CancellationToken.None);
    }

    /// <summary>The server sees a new connection: follow the board again, then refetch what may have changed unseen.</summary>
    private async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        await JoinWatchedBoardAsync(cancellationToken);
        BoardChanged?.Invoke();
        InboxChanged?.Invoke();
    }

    // The connection may still be in the group of a board watched earlier; only the one on screen matters.
    private void OnBoardChanged(Guid boardId)
    {
        if (boardId == watchedBoardId) BoardChanged?.Invoke();
    }

    private void OnInboxChanged() => InboxChanged?.Invoke();

    private void Set(LiveState state)
    {
        if (State == state) return;

        State = state;
        StateChanged?.Invoke();
    }
}
