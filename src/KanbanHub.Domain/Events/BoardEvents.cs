namespace KanbanHub.Domain.Events;

/// <summary>Something that happened to a card, as its transaction recorded it in <c>kb.Outbox</c>.</summary>
public sealed record BoardEvent(long Id, Guid BoardId, Guid CardId, string Type, string Payload)
{
    public const string CardCreated = "card.created";
    public const string CardUpdated = "card.updated";
    public const string CardMoved = "card.moved";
    public const string CardBlocked = "card.blocked";
    public const string CardUnblocked = "card.unblocked";
    public const string CardCommented = "card.commented";
}

/// <summary>Storage port for the outbox: events wait here until they are published.</summary>
public interface IOutboxStore
{
    /// <summary>The oldest unpublished events created at or before <paramref name="createdBefore"/>, in order.</summary>
    Task<IReadOnlyList<BoardEvent>> ListPendingAsync(int max, DateTimeOffset createdBefore, CancellationToken cancellationToken);

    Task MarkDispatchedAsync(IReadOnlyList<long> eventIds, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Hands an event to whoever consumes it: a Service Bus topic in Azure, the handlers themselves locally.</summary>
public interface IBoardEventPublisher
{
    Task PublishAsync(BoardEvent boardEvent, CancellationToken cancellationToken);
}

/// <summary>A consumer of board events. Events can arrive twice, so every handler must be idempotent.</summary>
public interface IBoardEventConsumer
{
    Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes what the outbox holds, oldest first (specification section 9.3). An event is marked published only
/// after it was handed over, so a crash in between repeats it rather than losing it.
/// </summary>
public sealed class OutboxDispatcher(IOutboxStore outbox, IBoardEventPublisher publisher, TimeProvider clock)
{
    public const int BatchSize = 100;

    /// <summary>
    /// Publishes pending events at least <paramref name="minimumAge"/> old and returns how many went out.
    /// The age lets a sweeper leave fresh events to the request that just wrote them.
    /// </summary>
    public async Task<int> DispatchAsync(TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<BoardEvent> pending = await outbox.ListPendingAsync(BatchSize, now - minimumAge, cancellationToken);
        List<long> published = [];

        try
        {
            foreach (BoardEvent boardEvent in pending)
            {
                await publisher.PublishAsync(boardEvent, cancellationToken);
                published.Add(boardEvent.Id);
            }
        }
        finally
        {
            if (published.Count > 0) await outbox.MarkDispatchedAsync(published, now, cancellationToken);
        }

        return published.Count;
    }
}

/// <summary>
/// Stands in for the topic and its subscriptions where there is no Service Bus (a development machine, tests):
/// every consumer handles the event in this process.
/// </summary>
public sealed class InProcessPublisher(IEnumerable<IBoardEventConsumer> consumers) : IBoardEventPublisher
{
    public async Task PublishAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        foreach (IBoardEventConsumer consumer in consumers)
            await consumer.HandleAsync(boardEvent, cancellationToken);
    }
}
