using System.Text.Json;
using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>What a move leaves behind: the card's new place, the movement row, the outbox event and the flow view.</summary>
public class MovementRecordTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    private static readonly CardPosition ParkingLot = CardPosition.OutsideSteps(Location.Parked);

    [Fact]
    public async Task A_move_is_recorded_with_where_who_and_how()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        Moved moved = await MustMoveAsync(card, Board.Implement.Active);

        MovementRecord movement = Assert.Single(await Records.MovementsAsync(card));
        Assert.Equal(moved.MovementId, movement.MovementId);
        Assert.Equal((Location.Done, Board.Specify.Id), (movement.FromLocation, movement.FromStepId!.Value));
        Assert.Equal((Location.Active, Board.Implement.Id), (movement.ToLocation, movement.ToStepId!.Value));
        Assert.Equal((MoveSource.User, TestBoard.Actor), (movement.Source, movement.ActorId));
        Assert.Null(movement.ViolationsJson);
    }

    [Fact]
    public async Task A_move_queues_a_card_moved_event_in_the_same_transaction()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        Moved moved = await MustMoveAsync(card, Board.Implement.Active);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(await Records.OutboxPayloadsAsync(card)));
        Assert.Equal(card, payload.RootElement.GetProperty("cardId").GetGuid());
        Assert.Equal(Board.Implement.Id, payload.RootElement.GetProperty("toStepId").GetGuid());
        Assert.Equal(moved.MovementId, payload.RootElement.GetProperty("movementId").GetInt64());
    }

    [Fact]
    public async Task A_rejected_move_leaves_no_movement_and_no_event()
    {
        await Board.SetLimitAsync(Board.Implement, 1);
        await Board.AddCardAsync(Board.Implement.Active);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        await MoveAsync(card, Board.Implement.Active);

        Assert.Empty(await Records.MovementsAsync(card));
        Assert.Empty(await Records.OutboxPayloadsAsync(card));
    }

    [Fact]
    public async Task A_parked_card_leaves_its_step_and_remembers_it()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);

        await MustMoveAsync(card, ParkingLot);

        CardSnapshot parked = await Records.CardAsync(card);
        Assert.Equal((Location.Parked, null, Board.Implement.Id), (parked.Location, parked.StepId, parked.ParkedFromStepId));
    }

    [Fact]
    public async Task The_repository_reads_a_card_together_with_its_workflow()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        await MustMoveAsync(card, ParkingLot);

        CardOnBoard onBoard = await Repository.GetOnBoardAsync(card, TestContext.Current.CancellationToken);

        Assert.Equal(Board.Id, onBoard.BoardId);
        Assert.Equal(CardPosition.OutsideSteps(Location.Parked, Board.Implement.Id), onBoard.Position);
        Assert.Equal(new MovePlan(onBoard.Position, Board.Implement.Active), onBoard.PlanMoveTo(Board.Implement.Id, Location.Active));
        Assert.Null(onBoard.PlanMoveTo(Board.Validate.Id, Location.Track));
    }

    [Fact]
    public async Task Reading_an_unknown_card_is_not_found() =>
        await Assert.ThrowsAsync<CardNotFoundException>(
            () => Repository.GetOnBoardAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_backward_move_records_why()
    {
        Guid card = await Board.AddCardAsync(Board.Validate.Active);

        await MustMoveAsync(card, Board.Implement.Active, new MoveOptions { Reason = "Failed validation" });

        Assert.Equal("Failed validation", Assert.Single(await Records.MovementsAsync(card)).Reason);
    }

    [Fact]
    public async Task The_flow_view_reports_when_work_started_and_finished()
    {
        var started = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var finished = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        Guid card = await Board.AddCardAsync(CardPosition.Backlog);

        await MustMoveAsync(card, Board.Specify.Active, new MoveOptions { Now = started });
        Assert.Equal(new FlowRecord(started.UtcDateTime, FinishedAtUtc: null), await Records.FlowAsync(card));

        await Board.AddCardAsync(Board.Validate.Active);   // a second card, so the view must tell them apart
        await FinishAsync(card, finished);

        Assert.Equal(new FlowRecord(started.UtcDateTime, finished.UtcDateTime), await Records.FlowAsync(card));
    }

    /// <summary>Walks a card from Specify Active to the final Done, finishing at <paramref name="finishedAt"/>.</summary>
    private async Task FinishAsync(Guid cardId, DateTimeOffset finishedAt)
    {
        CardPosition[] route = [Board.Specify.Done, Board.Implement.Active, Board.Implement.Done, Board.Validate.Active];
        foreach (CardPosition stop in route)
            await MustMoveAsync(cardId, stop, new MoveOptions { Now = finishedAt.AddHours(-1) });

        await MustMoveAsync(cardId, Board.Validate.Done, new MoveOptions { Now = finishedAt });
    }
}
