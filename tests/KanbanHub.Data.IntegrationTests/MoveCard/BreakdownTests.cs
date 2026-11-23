using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>Feature: breakdown counts as one in Specify (rule R2, specification section 19.2).</summary>
public class BreakdownTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    private Guid parentP;
    private Guid[] childrenOfP = [];

    [Fact]
    public async Task Children_count_as_their_parent_in_the_breakdown_step()
    {
        await ArrangeTwoFamiliesInSpecifyAsync();
        Guid newItem = await Board.AddCardAsync(CardPosition.Backlog);

        var full = Assert.IsType<WipLimitReached>((await MoveAsync(newItem, Board.Specify.Active)).Value);

        Assert.Equal(new WipViolation(WipScope.Step, "Specify", Limit: 2, Current: 2, WipPolicy.Strict), Assert.Single(full.Violations));
    }

    [Fact]
    public async Task One_more_child_of_a_parent_already_in_the_step_adds_nothing()
    {
        await ArrangeTwoFamiliesInSpecifyAsync();
        Guid lateChild = await Board.AddCardAsync(CardPosition.Backlog, parentP);

        await MustMoveAsync(lateChild, Board.Specify.Active);
    }

    [Fact]
    public async Task Downstream_each_child_counts_on_its_own()
    {
        await ArrangeTwoFamiliesInSpecifyAsync();
        await Board.SetLimitAsync(Board.Implement, 1);

        await MustMoveAsync(childrenOfP[0], Board.Implement.Active);
        var full = Assert.IsType<WipLimitReached>((await MoveAsync(childrenOfP[1], Board.Implement.Active)).Value);

        Assert.Equal(1, Assert.Single(full.Violations).Current);
    }

    /// <summary>Specify (limit 2) holds parent P with four children in Done, and parent Q in Active: 2 of 2.</summary>
    private async Task ArrangeTwoFamiliesInSpecifyAsync()
    {
        await Board.SetLimitAsync(Board.Specify, 2);
        parentP = await Board.AddCardAsync(Board.Specify.Done);
        childrenOfP = await Board.AddCardsAsync(4, Board.Specify.Done, parentP);
        await Board.AddCardAsync(Board.Specify.Active);
    }
}
