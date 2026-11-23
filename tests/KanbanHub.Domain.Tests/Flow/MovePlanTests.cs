using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

public class MovePlanTests
{
    private static readonly MovePlan Pull = new(Done(Specify), Active(Implement));
    private static readonly MovePlan Rework = new(Done(Implement), Active(Specify));

    [Fact]
    public void A_card_plans_a_move_to_a_column_of_its_board()
    {
        CardOnBoard card = CardAt(Done(Specify));

        MovePlan? plan = card.PlanMoveTo(Implement.Id, Location.Active);

        Assert.Equal(Pull, plan);
    }

    [Fact]
    public void A_card_cannot_plan_a_move_to_a_column_its_board_lacks() =>
        Assert.Null(CardAt(Done(Specify)).PlanMoveTo(Guid.NewGuid(), Location.Active));

    [Fact]
    public void A_forward_move_needs_no_extra_permission_and_no_reason()
    {
        Assert.False(Pull.IsBackward);
        Assert.Null(Pull.FindMissingPermission(Permission.CardMove));
        Assert.Null(Pull.FindIllegality(hasReason: false));
    }

    [Fact]
    public void A_backward_move_needs_the_backward_permission()
    {
        Assert.True(Rework.IsBackward);
        Assert.Equal(Permission.CardMoveBackward, Rework.FindMissingPermission(Permission.CardMove));
        Assert.Null(Rework.FindMissingPermission(Permission.CardMove | Permission.CardMoveBackward));
    }

    [Fact]
    public void A_backward_move_needs_a_reason()
    {
        Assert.Equal(MovePlan.ReworkNeedsReason, Rework.FindIllegality(hasReason: false));
        Assert.Null(Rework.FindIllegality(hasReason: true));
    }

    [Fact]
    public void An_illegal_move_reports_the_transition_rule_it_breaks()
    {
        var skip = new MovePlan(CardPosition.Backlog, Active(Validate));

        Assert.Equal("A move from Backlog to Active isn't allowed on this board.", skip.FindIllegality(hasReason: true));
    }

    [Fact]
    public void Dropping_a_card_where_it_already_is_is_not_a_move()
    {
        var samePlace = new MovePlan(Active(Implement), Active(Implement));

        Assert.False(samePlace.IsBackward);
        Assert.NotNull(samePlace.FindIllegality(hasReason: true));
    }
}
