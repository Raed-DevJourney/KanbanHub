using KanbanHub.Domain.Events;

namespace KanbanHub.Domain.Tests.Events;

/// <summary>How an event reaches the people watching a board, and what stands in for the topic without one.</summary>
public class BroadcastTests
{
    private static readonly BoardEvent Moved = new(7, Guid.NewGuid(), Guid.NewGuid(), BoardEvent.CardMoved, "{}");

    private readonly RecordingGateway gateway = new();
    private readonly RecordingPublisher topic = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_card_event_tells_the_boards_watchers_which_card_changed_and_how()
    {
        await new BoardBroadcaster(gateway).HandleAsync(Moved, Ct);

        Assert.Equal([new BoardChanged(Moved.BoardId, BoardEvent.CardMoved, Moved.CardId)], gateway.BoardChanges);
    }

    [Fact]
    public void Each_board_has_its_own_group()
    {
        var boardId = new Guid("5b1d7a52-7a0e-4c0b-9a51-0d6a1c0f3c11");

        Assert.Equal("board:5b1d7a52-7a0e-4c0b-9a51-0d6a1c0f3c11", IRealtimeGateway.GroupOf(boardId));
    }

    [Fact]
    public async Task Publishing_tells_the_watchers_and_then_feeds_the_topic()
    {
        await Publisher().PublishAsync(Moved, Ct);

        Assert.Single(gateway.BoardChanges);
        Assert.Equal([Moved], topic.Published);
    }

    [Fact]
    public async Task An_event_the_watchers_could_not_be_told_about_is_not_handed_on_so_the_outbox_keeps_it()
    {
        gateway.IsDown = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher().PublishAsync(Moved, Ct));

        Assert.Empty(topic.Published);
    }

    [Fact]
    public async Task Without_a_topic_every_consumer_handles_the_event_in_process()
    {
        var first = new CountingConsumer();
        var second = new CountingConsumer();

        await new InProcessPublisher([first, second]).PublishAsync(Moved, Ct);

        Assert.Equal((1, 1), (first.Handled, second.Handled));
    }

    private BroadcastingPublisher Publisher() => new(new BoardBroadcaster(gateway), topic);

    private sealed class CountingConsumer : IBoardEventConsumer
    {
        public int Handled { get; private set; }

        public Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
        {
            Handled++;
            return Task.CompletedTask;
        }
    }
}
