using KanbanHub.Data.Events;
using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.Events;

/// <summary>
/// The outbox against the real table. The tests of a class run one after another, and only this class
/// marks events as published, so each test sees its own events until it publishes them itself.
/// </summary>
public class OutboxStoreTests(KanbanDatabase database)
{
    private const int NoCap = 100_000;

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly OutboxStore store = new(database.Connections);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pending_events_come_oldest_first_and_leave_once_they_are_marked()
    {
        (TestBoard board, Guid card) = await BoardWithCardAsync();
        await WriteAsync(board, card, BoardEvent.CardBlocked, Now.AddSeconds(-60));
        await WriteAsync(board, card, BoardEvent.CardUnblocked, Now.AddSeconds(-50));

        List<BoardEvent> pending = await PendingOfAsync(board, createdBefore: Now);
        await store.MarkDispatchedAsync([.. pending.Select(waiting => waiting.Id)], Now, Ct);

        Assert.Equal([BoardEvent.CardBlocked, BoardEvent.CardUnblocked], pending.Select(waiting => waiting.Type));
        Assert.All(pending, waiting => Assert.Equal((card, PayloadOf(card)), (waiting.CardId, waiting.Payload)));
        Assert.Empty(await PendingOfAsync(board, createdBefore: Now));
    }

    [Fact]
    public async Task Events_younger_than_the_cutoff_wait_for_the_next_sweep()
    {
        (TestBoard board, Guid card) = await BoardWithCardAsync();
        await WriteAsync(board, card, BoardEvent.CardBlocked, Now.AddSeconds(-10));

        Assert.Empty(await PendingOfAsync(board, createdBefore: Now.AddSeconds(-30)));
        Assert.Single(await PendingOfAsync(board, createdBefore: Now));
    }

    [Fact]
    public async Task The_dispatcher_publishes_each_stored_event_once()
    {
        (TestBoard board, Guid card) = await BoardWithCardAsync();
        await WriteAsync(board, card, BoardEvent.CardBlocked, Now.AddSeconds(-60));
        await WriteAsync(board, card, BoardEvent.CardUnblocked, Now.AddSeconds(-50));
        var publisher = new CollectingPublisher();
        var dispatcher = new OutboxDispatcher(store, publisher, TimeProvider.System);

        while (await dispatcher.DispatchAsync(TimeSpan.Zero, Ct) > 0)
        {
            // Other tests keep writing events; go on until the outbox is empty.
        }

        await dispatcher.DispatchAsync(TimeSpan.Zero, Ct);

        Assert.Equal(
            [BoardEvent.CardBlocked, BoardEvent.CardUnblocked],
            publisher.Published.Where(sent => sent.BoardId == board.Id).Select(sent => sent.Type));
    }

    private async Task<(TestBoard Board, Guid Card)> BoardWithCardAsync()
    {
        TestBoard board = await TestBoard.CreateAsync(database.Connections);
        return (board, await board.AddCardAsync(CardPosition.Backlog));
    }

    private async Task<List<BoardEvent>> PendingOfAsync(TestBoard board, DateTimeOffset createdBefore) =>
        [.. (await store.ListPendingAsync(NoCap, createdBefore, Ct)).Where(waiting => waiting.BoardId == board.Id)];

    private Task WriteAsync(TestBoard board, Guid card, string eventType, DateTimeOffset at) =>
        database.Connections.ExecuteAsync("""
            INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
            VALUES (@CardId, @BoardId, @EventType, @Payload, @At);
            """,
            new { CardId = card, BoardId = board.Id, EventType = eventType, Payload = PayloadOf(card), At = at.UtcDateTime });

    private static string PayloadOf(Guid card) => $$"""{"cardId":"{{card}}"}""";

    private sealed class CollectingPublisher : IBoardEventPublisher
    {
        public List<BoardEvent> Published { get; } = [];

        public Task PublishAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
        {
            Published.Add(boardEvent);
            return Task.CompletedTask;
        }
    }
}
