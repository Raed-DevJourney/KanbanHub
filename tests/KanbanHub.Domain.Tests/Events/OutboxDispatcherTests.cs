using KanbanHub.Domain.Events;

namespace KanbanHub.Domain.Tests.Events;

public class OutboxDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

    private readonly RecordingPublisher publisher = new();

    [Fact]
    public async Task Pending_events_are_published_oldest_first_and_then_marked()
    {
        var outbox = new FakeOutbox(Event(1), Event(2), Event(3));

        int published = await DispatchAsync(outbox, TimeSpan.Zero);

        Assert.Equal(3, published);
        Assert.Equal([1, 2, 3], publisher.Published.Select(sent => sent.Id));
        Assert.Equal([1, 2, 3], outbox.Dispatched);
        Assert.Equal(Now, outbox.MarkedAt);
    }

    [Fact]
    public async Task A_sweep_leaves_young_events_to_the_request_that_wrote_them()
    {
        var outbox = new FakeOutbox();

        await DispatchAsync(outbox, TimeSpan.FromSeconds(30));

        Assert.Equal((OutboxDispatcher.BatchSize, Now.AddSeconds(-30)), (outbox.AskedFor, outbox.AskedBefore));
    }

    [Fact]
    public async Task When_publishing_fails_what_went_out_is_marked_and_the_rest_waits()
    {
        var outbox = new FakeOutbox(Event(1), Event(2), Event(3));
        publisher.FailsOn = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => DispatchAsync(outbox, TimeSpan.Zero));

        Assert.Equal([1], outbox.Dispatched);
    }

    [Fact]
    public async Task An_empty_outbox_is_left_alone()
    {
        var outbox = new FakeOutbox();

        int published = await DispatchAsync(outbox, TimeSpan.Zero);

        Assert.Equal(0, published);
        Assert.Null(outbox.MarkedAt);
    }

    private Task<int> DispatchAsync(FakeOutbox outbox, TimeSpan minimumAge) =>
        new OutboxDispatcher(outbox, publisher, new FixedClock(Now)).DispatchAsync(minimumAge, TestContext.Current.CancellationToken);

    private static BoardEvent Event(long id) => new(id, Guid.NewGuid(), Guid.NewGuid(), BoardEvent.CardMoved, "{}");

    private sealed class FakeOutbox(params BoardEvent[] pending) : IOutboxStore
    {
        public List<long> Dispatched { get; } = [];

        public DateTimeOffset? MarkedAt { get; private set; }

        public int? AskedFor { get; private set; }

        public DateTimeOffset? AskedBefore { get; private set; }

        public Task<IReadOnlyList<BoardEvent>> ListPendingAsync(int max, DateTimeOffset createdBefore, CancellationToken cancellationToken)
        {
            (AskedFor, AskedBefore) = (max, createdBefore);
            return Task.FromResult<IReadOnlyList<BoardEvent>>(pending);
        }

        public Task MarkDispatchedAsync(IReadOnlyList<long> eventIds, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Dispatched.AddRange(eventIds);
            MarkedAt = now;
            return Task.CompletedTask;
        }
    }
}
