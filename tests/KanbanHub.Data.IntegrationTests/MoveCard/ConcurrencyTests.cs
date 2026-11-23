using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>Rules R9 (concurrency) and R10 (idempotency), including the race test of specification section 19.1.</summary>
public class ConcurrencyTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    [Fact]
    public async Task A_hundred_parallel_pulls_into_two_free_slots_fill_the_step_exactly()
    {
        await Board.SetLimitAsync(Board.Implement, 5);
        await Board.AddCardsAsync(3, Board.Implement.Active);
        Guid[] waiting = await Board.AddCardsAsync(100, Board.Specify.Done);

        MoveOutcome[] outcomes = await Task.WhenAll(
            waiting.Select(card => Task.Run(() => MoveAsync(card, Board.Implement.Active))));

        Assert.Equal(2, outcomes.Count(outcome => outcome.Value is Moved));
        Assert.Equal(98, outcomes.Count(outcome => outcome.Value is WipLimitReached));
        Assert.Equal(5, await Records.ActiveAndDoneAsync(Board.Implement));
    }

    [Fact]
    public async Task A_stale_row_version_is_a_conflict_and_changes_nothing()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);
        byte[] seenByTheClient = (await Records.CardAsync(card)).RowVersion;
        Moved first = await MustMoveAsync(card, Board.Implement.Active);

        MoveOutcome outcome = await MoveAsync(card, Board.Implement.Done, new MoveOptions { ExpectedRowVersion = seenByTheClient });

        var conflict = Assert.IsType<ConcurrencyConflict>(outcome.Value);
        Assert.Equal(first.RowVersion, conflict.CurrentRowVersion);
        Assert.Equal(Location.Active, (await Records.CardAsync(card)).Location);
    }

    [Fact]
    public async Task A_successful_move_returns_the_cards_new_row_version()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        Moved moved = await MustMoveAsync(card, Board.Implement.Active);

        Assert.Equal((await Records.CardAsync(card)).RowVersion, moved.RowVersion);
        Assert.Equal(Board.Id, moved.BoardId);
    }

    [Fact]
    public async Task A_retried_request_returns_the_original_result_and_moves_once()
    {
        await Board.SetLimitAsync(Board.Implement, 1, WipPolicy.Warn);
        await Board.AddCardAsync(Board.Implement.Active);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);
        var request = new MoveOptions { ExpectedRowVersion = (await Records.CardAsync(card)).RowVersion };

        Moved original = await MustMoveAsync(card, Board.Implement.Active, request);
        Moved retried = await MustMoveAsync(card, Board.Implement.Active, request);

        Assert.Equal(original.MovementId, retried.MovementId);
        Assert.Equal(original.Recorded, retried.Recorded);
        Assert.Single(await Records.MovementsAsync(card));
    }

    [Fact]
    public async Task The_same_request_sent_twice_at_once_still_moves_once()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);
        var request = new MoveOptions { ExpectedRowVersion = (await Records.CardAsync(card)).RowVersion };

        Moved[] results = await Task.WhenAll(
            Task.Run(() => MustMoveAsync(card, Board.Implement.Active, request)),
            Task.Run(() => MustMoveAsync(card, Board.Implement.Active, request)));

        Assert.Equal(results[0].MovementId, results[1].MovementId);
        Assert.Single(await Records.MovementsAsync(card));
    }

    [Fact]
    public async Task An_unknown_card_is_not_found()
    {
        var request = new MoveOptions { ExpectedRowVersion = new byte[8] };

        await Assert.ThrowsAsync<CardNotFoundException>(() => MoveAsync(Guid.NewGuid(), Board.Implement.Active, request));
    }

    [Fact]
    public async Task A_step_of_another_board_is_not_a_legal_target()
    {
        TestBoard otherBoard = await TestBoard.CreateAsync(Connections);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        MoveOutcome outcome = await MoveAsync(card, otherBoard.Implement.Active);

        Assert.Equal(IllegalTransition.UnknownColumn, outcome.Value);
        Assert.Equal(Board.Specify.Id, (await Records.CardAsync(card)).StepId);
    }

    [Fact]
    public async Task Track_is_not_a_legal_target_where_the_step_has_none()
    {
        Guid card = await Board.AddCardAsync(Board.Validate.Active);

        MoveOutcome outcome = await MoveAsync(card, Board.Validate.Track);

        Assert.Equal(IllegalTransition.UnknownColumn, outcome.Value);
    }
}
