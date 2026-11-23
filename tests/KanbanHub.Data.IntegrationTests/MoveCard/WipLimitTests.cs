using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>Feature: WIP limits are enforced (specification sections 5.1 and 19.2).</summary>
public class WipLimitTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    [Fact]
    public async Task Pull_into_a_full_step_is_rejected()
    {
        await Board.SetLimitAsync(Board.Implement, 5);
        await Board.AddCardsAsync(3, Board.Implement.Active);
        await Board.AddCardsAsync(2, Board.Implement.Done);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        MoveOutcome outcome = await MoveAsync(card, Board.Implement.Active);

        var full = Assert.IsType<WipLimitReached>(outcome.Value);
        Assert.Equal([new WipViolation(WipScope.Step, "Implement", Limit: 5, Current: 5, WipPolicy.Strict)], full.Violations);
        Assert.Equal(3, full.SwarmCandidates.Count);
        Assert.Equal(Board.Specify.Done.Location, (await Records.CardAsync(card)).Location);
        Assert.Empty(await Records.MovementsAsync(card));
    }

    [Fact]
    public async Task Swarm_candidates_put_blocked_cards_first_then_the_oldest()
    {
        await Board.SetLimitAsync(Board.Implement, 3);
        Guid[] inProgress = await Board.AddCardsAsync(3, Board.Implement.Active);
        await Board.BlockAsync(inProgress[2]);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        var full = Assert.IsType<WipLimitReached>((await MoveAsync(card, Board.Implement.Active)).Value);

        Assert.Equal([inProgress[2], inProgress[0], inProgress[1]], full.SwarmCandidates);
    }

    [Fact]
    public async Task Finishing_within_a_step_is_never_WIP_blocked()
    {
        await Board.SetLimitAsync(Board.Implement, 5);
        Guid[] inProgress = await Board.AddCardsAsync(5, Board.Implement.Active);

        await MustMoveAsync(inProgress[0], Board.Implement.Done);

        Assert.Equal(5, await Records.ActiveAndDoneAsync(Board.Implement));
    }

    [Fact]
    public async Task Tracked_cards_dont_count()
    {
        await Board.SetLimitAsync(Board.Implement, 5);
        Guid[] inProgress = await Board.AddCardsAsync(5, Board.Implement.Active);
        Guid waiting = await Board.AddCardAsync(Board.Specify.Done);

        await MustMoveAsync(inProgress[0], Board.Implement.Track);

        Assert.Equal(4, await Records.ActiveAndDoneAsync(Board.Implement));
        await MustMoveAsync(waiting, Board.Implement.Active);
    }

    [Fact]
    public async Task A_tracked_card_returns_only_when_a_slot_is_open()
    {
        await Board.SetLimitAsync(Board.Implement, 1);
        Guid tracked = await Board.AddCardAsync(Board.Implement.Track);
        Guid inProgress = await Board.AddCardAsync(Board.Implement.Active);

        Assert.IsType<WipLimitReached>((await MoveAsync(tracked, Board.Implement.Active)).Value);

        await MustMoveAsync(inProgress, Board.Implement.Done);
        await MustMoveAsync(inProgress, Board.Validate.Active);
        await MustMoveAsync(tracked, Board.Implement.Active);
    }

    [Fact]
    public async Task A_parked_card_returns_only_when_a_slot_is_open()
    {
        await Board.SetLimitAsync(Board.Implement, 1);
        Guid parked = await Board.AddCardAsync(Board.Implement.Active);
        await MustMoveAsync(parked, CardPosition.OutsideSteps(Location.Parked));
        await Board.AddCardAsync(Board.Implement.Active);

        MoveOutcome outcome = await MoveAsync(parked, Board.Implement.Active);

        Assert.IsType<WipLimitReached>(outcome.Value);
    }

    [Fact]
    public async Task Override_with_a_reason()
    {
        await Board.SetLimitAsync(Board.Validate, 3, WipPolicy.AllowWithReason);
        await Board.AddCardsAsync(3, Board.Validate.Active);
        Guid hotfix = await Board.AddCardAsync(Board.Implement.Done);
        var expected = new WipViolation(WipScope.Step, "Validate", Limit: 3, Current: 3, WipPolicy.AllowWithReason);

        var needsReason = Assert.IsType<OverrideReasonRequired>((await MoveAsync(hotfix, Board.Validate.Active)).Value);
        Assert.Equal([expected], needsReason.Violations);

        Moved moved = await MustMoveAsync(hotfix, Board.Validate.Active, new MoveOptions { OverrideReason = "Customer hotfix" });

        Assert.Equal([expected], moved.Recorded);
        MovementRecord audit = Assert.Single(await Records.MovementsAsync(hotfix));
        Assert.Equal("Customer hotfix", audit.OverrideReason);
        Assert.Contains("\"ScopeName\":\"Validate\"", audit.ViolationsJson);
    }

    [Fact]
    public async Task A_reason_cannot_override_a_strict_limit()
    {
        await Board.SetLimitAsync(Board.Validate, 1);
        await Board.AddCardAsync(Board.Validate.Active);
        Guid card = await Board.AddCardAsync(Board.Implement.Done);

        MoveOutcome outcome = await MoveAsync(card, Board.Validate.Active, new MoveOptions { OverrideReason = "Please" });

        Assert.IsType<WipLimitReached>(outcome.Value);
    }

    [Fact]
    public async Task An_override_reason_is_not_recorded_when_nothing_was_overridden()
    {
        await Board.SetLimitAsync(Board.Validate, 3, WipPolicy.AllowWithReason);
        Guid card = await Board.AddCardAsync(Board.Implement.Done);

        Moved moved = await MustMoveAsync(card, Board.Validate.Active, new MoveOptions { OverrideReason = "Just in case" });

        Assert.Empty(moved.Recorded);
        Assert.Null(Assert.Single(await Records.MovementsAsync(card)).OverrideReason);
    }

    [Fact]
    public async Task A_warn_limit_lets_the_move_through_and_records_it()
    {
        await Board.SetLimitAsync(Board.Implement, 1, WipPolicy.Warn);
        await Board.AddCardAsync(Board.Implement.Active);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        Moved moved = await MustMoveAsync(card, Board.Implement.Active);

        Assert.Equal([new WipViolation(WipScope.Step, "Implement", Limit: 1, Current: 1, WipPolicy.Warn)], moved.Recorded);
        Assert.NotNull(Assert.Single(await Records.MovementsAsync(card)).ViolationsJson);
    }

    [Fact]
    public async Task A_limit_lowered_below_the_count_takes_no_pull_until_the_step_drains()
    {
        Guid[] inProgress = await Board.AddCardsAsync(3, Board.Implement.Active);
        Guid waiting = await Board.AddCardAsync(Board.Specify.Done);
        await Board.SetLimitAsync(Board.Implement, 2);

        var overLimit = Assert.IsType<WipLimitReached>((await MoveAsync(waiting, Board.Implement.Active)).Value);
        Assert.Equal(3, Assert.Single(overLimit.Violations).Current);

        await DrainAsync(inProgress[0]);
        Assert.IsType<WipLimitReached>((await MoveAsync(waiting, Board.Implement.Active)).Value);

        await DrainAsync(inProgress[1]);
        await MustMoveAsync(waiting, Board.Implement.Active);
    }

    [Fact]
    public async Task The_final_steps_Done_never_counts()
    {
        await Board.SetLimitAsync(Board.Validate, 1);
        await Board.AddCardsAsync(10, Board.Validate.Done);
        Guid card = await Board.AddCardAsync(Board.Implement.Done);

        await MustMoveAsync(card, Board.Validate.Active);
        await MustMoveAsync(card, Board.Validate.Done);
    }

    [Fact]
    public async Task Reopening_finished_work_in_the_final_step_needs_a_free_slot()
    {
        await Board.SetLimitAsync(Board.Validate, 1);
        Guid finished = await Board.AddCardAsync(Board.Validate.Done);
        await Board.AddCardAsync(Board.Validate.Active);

        MoveOutcome outcome = await MoveAsync(finished, Board.Validate.Active, new MoveOptions { Reason = "Regression" });

        Assert.IsType<WipLimitReached>(outcome.Value);
    }

    [Fact]
    public async Task A_step_without_a_limit_takes_every_pull()
    {
        await Board.AddCardsAsync(20, Board.Implement.Active);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        Moved moved = await MustMoveAsync(card, Board.Implement.Active);

        Assert.Empty(moved.Recorded);
    }

    /// <summary>Takes a card out of Implement by finishing it there and pulling it into Validate.</summary>
    private async Task DrainAsync(Guid cardId)
    {
        await MustMoveAsync(cardId, Board.Implement.Done);
        await MustMoveAsync(cardId, Board.Validate.Active);
    }
}
