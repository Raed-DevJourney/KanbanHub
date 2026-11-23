using KanbanHub.Data.Cards;
using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>FR-C03: a broken-down parent follows its tasks: beside them, then a roll-up, then done.</summary>
public class RollUpTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    private static readonly CardPosition Canceled = CardPosition.OutsideSteps(Location.Canceled);

    private Guid parent;
    private IReadOnlyList<Guid> tasks = [];

    [Fact]
    public async Task Tasks_are_created_beside_their_parent_and_add_no_wip()
    {
        await Board.SetLimitAsync(Board.Specify, 1);
        await BreakDownInSpecifyAsync("Design the API", "Write the tests");

        Assert.All(tasks, task => Assert.Equal(Board.Specify.Id, Records.CardAsync(task).Result.StepId));
        Assert.Equal(3, await Records.ActiveAndDoneAsync(Board.Specify));
        Guid newItem = await Board.AddCardAsync(CardPosition.Backlog);
        Assert.IsType<WipLimitReached>((await MoveAsync(newItem, Board.Specify.Active)).Value);
    }

    [Fact]
    public async Task A_parent_that_finishes_the_breakdown_step_takes_its_tasks_along()
    {
        await BreakDownInSpecifyAsync("Design the API", "Write the tests");

        await MustMoveAsync(parent, Board.Specify.Done);

        Assert.All(tasks, task => Assert.Equal(Location.Done, Records.CardAsync(task).Result.Location));
    }

    [Fact]
    public async Task The_parent_becomes_a_roll_up_when_its_last_task_leaves_the_step()
    {
        await BreakDownInSpecifyAsync("Design the API", "Write the tests");
        await MustMoveAsync(parent, Board.Specify.Done);

        await MustMoveAsync(tasks[0], Board.Implement.Active);
        Assert.Equal(Location.Done, (await Records.CardAsync(parent)).Location);

        await MustMoveAsync(tasks[1], Board.Implement.Active);

        CardSnapshot rolledUp = await Records.CardAsync(parent);
        Assert.Equal((Location.Rollup, null), (rolledUp.Location, rolledUp.StepId));
        Assert.Equal(MoveSource.Automation, (await Records.MovementsAsync(parent))[^1].Source);
    }

    [Fact]
    public async Task The_roll_up_completes_when_every_task_is_finished_or_canceled()
    {
        await BreakDownInSpecifyAsync("Design the API", "Write the tests", "Nice to have");
        await MustMoveAsync(parent, Board.Specify.Done);
        await MustMoveAsync(tasks[2], Canceled, new MoveOptions { Reason = "Out of scope" });

        await FinishAsync(tasks[0]);
        Assert.Equal(Board.Specify.Id, (await Records.CardAsync(parent)).StepId);   // a task is still waiting to be pulled

        await FinishAsync(tasks[1]);

        CardSnapshot completed = await Records.CardAsync(parent);
        Assert.Equal((Location.Done, Board.Validate.Id), (completed.Location, completed.StepId!.Value));
    }

    private async Task BreakDownInSpecifyAsync(params string[] titles)
    {
        parent = await Board.AddCardAsync(Board.Specify.Active);
        var stamp = new Stamp(TestBoard.Actor, DateTimeOffset.UtcNow);
        tasks = await new CardStore(Connections).BreakDownAsync(parent, titles, stamp, TestContext.Current.CancellationToken);
    }

    /// <summary>Walks a task from Specify Done to the final step's Done.</summary>
    private async Task FinishAsync(Guid task)
    {
        CardPosition[] route = [Board.Implement.Active, Board.Implement.Done, Board.Validate.Active, Board.Validate.Done];
        foreach (CardPosition stop in route.Where(stop => !IsBehind(stop, task)))
            await MustMoveAsync(task, stop);
    }

    /// <summary>True when the task is already at or past this stop (the first task was pulled earlier).</summary>
    private bool IsBehind(CardPosition stop, Guid task)
    {
        CardSnapshot card = Records.CardAsync(task).Result;
        return card.StepId == stop.StepId && card.Location == stop.Location;
    }
}
