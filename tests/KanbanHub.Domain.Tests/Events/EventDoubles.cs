using KanbanHub.Domain.Events;

namespace KanbanHub.Domain.Tests.Events;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Remembers what was pushed to browsers.</summary>
internal sealed class RecordingGateway : IRealtimeGateway
{
    public List<BoardChanged> BoardChanges { get; } = [];

    public List<Guid> InboxPushes { get; } = [];

    public bool IsDown { get; set; }

    public Task<RealtimeConnection> NegotiateAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task JoinBoardAsync(string connectionId, Guid boardId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task BoardChangedAsync(BoardChanged change, CancellationToken cancellationToken)
    {
        if (IsDown) throw new InvalidOperationException("The hub is down.");

        BoardChanges.Add(change);
        return Task.CompletedTask;
    }

    public Task InboxChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        InboxPushes.Add(userId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingPublisher : IBoardEventPublisher
{
    public List<BoardEvent> Published { get; } = [];

    /// <summary>Publishing the event with this id fails, as a broker outage would.</summary>
    public long? FailsOn { get; set; }

    public Task PublishAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        if (boardEvent.Id == FailsOn) throw new InvalidOperationException("The topic is unreachable.");

        Published.Add(boardEvent);
        return Task.CompletedTask;
    }
}
