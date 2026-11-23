using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>
/// A blocked card is set aside in its step's Blocked sub-column (the book's Track), where it stands out and
/// doesn't count toward WIP; unblocking brings it back when the step has room.
/// </summary>
public class BlockingTests(KanbanDatabase database)
{
    private static readonly Blocker WaitingForKeys = new("Waiting for the partner's keys", BlockerCategory.External);

    [Fact]
    public async Task Blocking_a_card_in_active_moves_it_to_the_steps_blocked_sub_column_and_frees_its_slot()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Implement, Location.Active);
        await board.AddCardAtAsync(board.Implement, Location.Active);
        HttpRequest request = Requests.From(board.Owner, WaitingForKeys);

        IActionResult blocked = await board.Cards.Block(request, card, Ct);

        CardDto after = (await board.CardAsync(card)).Card;
        Assert.Equal((200, BlockPlacement.MovedToBlocked), (blocked.StatusOf(), blocked.ValueOf<BlockResponse>().Placement));
        Assert.Equal((Location.Track, board.Implement.Id, true), (after.Location, after.StepId!.Value, after.IsBlocked));
        Assert.Equal($"\"{after.ETag}\"", request.HttpContext.Response.Headers.ETag.ToString());
        Assert.Equal(1, (await board.SnapshotAsync()).Steps[1].WipCount);
    }

    [Fact]
    public async Task Unblocking_brings_the_card_back_to_active_when_the_step_has_room()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Implement, Location.Active);
        await board.Cards.Block(Requests.From(board.Owner, WaitingForKeys), card, Ct);

        IActionResult unblocked = await board.Cards.Unblock(Requests.From(board.Owner), card, Ct);

        CardDto after = (await board.CardAsync(card)).Card;
        Assert.Equal(BlockPlacement.ReturnedToActive, unblocked.ValueOf<BlockResponse>().Placement);
        Assert.Equal((Location.Active, false), (after.Location, after.IsBlocked));
    }

    [Fact]
    public async Task Unblocking_into_a_full_step_leaves_the_card_waiting_in_blocked_until_a_slot_opens()
    {
        BoardScenario board = await StartAsync(database);
        await board.SetLimitsAsync(board.Owner, new StepLimit(board.Implement.Id, 1, WipPolicy.Strict));
        Guid card = await board.AddCardAtAsync(board.Implement, Location.Active);
        await board.Cards.Block(Requests.From(board.Owner, WaitingForKeys), card, Ct);
        Guid tookItsSlot = await board.AddCardAtAsync(board.Implement, Location.Active);

        IActionResult unblocked = await board.Cards.Unblock(Requests.From(board.Owner), card, Ct);
        CardDto waiting = (await board.CardAsync(card)).Card;
        await board.MustMoveAsync(tookItsSlot, board.Implement, Location.Done);
        await board.MustMoveAsync(tookItsSlot, board.Validate, Location.Active);   // the slot opens
        IActionResult pulledBack = await board.MoveAsync(board.Owner, card, board.Implement, Location.Active);

        Assert.Equal(BlockPlacement.WaitingForSlot, unblocked.ValueOf<BlockResponse>().Placement);
        Assert.Equal((Location.Track, false), (waiting.Location, waiting.IsBlocked));
        Assert.Equal(200, pulledBack.StatusOf());
    }

    [Fact]
    public async Task A_card_that_is_not_in_active_keeps_its_place_when_blocked()
    {
        BoardScenario board = await StartAsync(database);
        Guid waitingForPull = await board.AddCardAtAsync(board.Specify, Location.Done);

        IActionResult blocked = await board.Cards.Block(Requests.From(board.Owner, WaitingForKeys), waitingForPull, Ct);
        IActionResult unblocked = await board.Cards.Unblock(Requests.From(board.Owner), waitingForPull, Ct);

        Assert.Equal(BlockPlacement.Unmoved, blocked.ValueOf<BlockResponse>().Placement);
        Assert.Equal(BlockPlacement.Unmoved, unblocked.ValueOf<BlockResponse>().Placement);
        Assert.Equal(Location.Done, (await board.CardAsync(waitingForPull)).Card.Location);
    }

    [Fact]
    public async Task In_a_step_without_a_blocked_sub_column_the_card_stays_in_active_with_its_flag()
    {
        BoardScenario board = await StartAsync(database);
        await TurnOffBlockedSubColumnsAsync(board);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);

        IActionResult blocked = await board.Cards.Block(Requests.From(board.Owner, WaitingForKeys), card, Ct);

        CardDto after = (await board.CardAsync(card)).Card;
        Assert.Equal(BlockPlacement.Unmoved, blocked.ValueOf<BlockResponse>().Placement);
        Assert.Equal((Location.Active, true), (after.Location, after.IsBlocked));
    }

    private static async Task TurnOffBlockedSubColumnsAsync(BoardScenario board)
    {
        StepDraft[] steps =
        [
            .. (await board.SnapshotAsync()).Steps.Select(step =>
                new StepDraft(step.Id, step.Name, step.Color) { HasTrack = false, CountsParentsOnly = step.CountsParentsOnly })
        ];
        IActionResult saved = await board.Setup.SetWorkflow(Requests.From(board.Owner, new SetWorkflowRequest(steps)), board.BoardId, Ct);
        Assert.Equal(204, saved.StatusOf());
    }
}
