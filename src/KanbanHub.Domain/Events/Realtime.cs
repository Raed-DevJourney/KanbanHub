namespace KanbanHub.Domain.Events;

/// <summary>What a browser needs to open its real-time connection.</summary>
public sealed record RealtimeConnection(string Url, string? AccessToken);

/// <summary>Tells everyone watching a board that it changed, so they show the new state within a second (FR-M06).</summary>
public sealed record BoardChanged(Guid BoardId, string EventType, Guid CardId);

/// <summary>
/// The real-time channel to browsers: Azure SignalR Service in the cloud, a local hub on a development machine.
/// </summary>
public interface IRealtimeGateway
{
    /// <summary>The client method a board change arrives on, with the board id, the event type and the card id.</summary>
    const string BoardChangedMethod = "boardChanged";

    /// <summary>The client method that says "your inbox changed".</summary>
    const string InboxChangedMethod = "inboxChanged";

    Task<RealtimeConnection> NegotiateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Subscribes one connection to a board's changes. The caller has already checked <c>board.read</c>.</summary>
    Task JoinBoardAsync(string connectionId, Guid boardId, CancellationToken cancellationToken);

    Task BoardChangedAsync(BoardChanged change, CancellationToken cancellationToken);

    Task InboxChangedAsync(Guid userId, CancellationToken cancellationToken);

    static string GroupOf(Guid boardId) => $"board:{boardId}";
}

/// <summary>Pushes every card event to the people watching its board.</summary>
public sealed class BoardBroadcaster(IRealtimeGateway realtime) : IBoardEventConsumer
{
    public Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken) =>
        realtime.BoardChangedAsync(new BoardChanged(boardEvent.BoardId, boardEvent.Type, boardEvent.CardId), cancellationToken);
}

/// <summary>
/// Tells the board's watchers at once and then hands the event on, so a move shows up within a second (FR-M06)
/// without waiting for the consumers behind the topic. If either step fails the event stays in the outbox
/// and the sweeper repeats both.
/// </summary>
public sealed class BroadcastingPublisher(BoardBroadcaster broadcaster, IBoardEventPublisher next) : IBoardEventPublisher
{
    public async Task PublishAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        await broadcaster.HandleAsync(boardEvent, cancellationToken);
        await next.PublishAsync(boardEvent, cancellationToken);
    }
}
