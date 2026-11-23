using KanbanHub.Domain.Flow;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

public class WorkflowTests
{
    [Fact]
    public void A_target_in_a_step_carries_the_steps_order()
    {
        CardPosition? target = BoardWorkflow.ResolveTarget(Implement.Id, Location.Done);

        Assert.Equal(Done(Implement), target);
        Assert.Equal(2, target!.StepOrder);
    }

    [Fact]
    public void A_step_from_another_board_is_not_a_target() =>
        Assert.Null(BoardWorkflow.ResolveTarget(Guid.NewGuid(), Location.Active));

    [Fact]
    public void A_step_sub_column_without_a_step_is_not_a_target() =>
        Assert.Null(BoardWorkflow.ResolveTarget(stepId: null, Location.Active));

    [Fact]
    public void Track_is_a_target_only_where_the_step_offers_it()
    {
        Assert.NotNull(BoardWorkflow.ResolveTarget(Implement.Id, Location.Track));
        Assert.Null(BoardWorkflow.ResolveTarget(Validate.Id, Location.Track));
    }

    [Fact]
    public void Targets_outside_steps_ignore_the_step()
    {
        CardPosition? target = BoardWorkflow.ResolveTarget(Implement.Id, Location.Parked);

        Assert.Equal(CardPosition.OutsideSteps(Location.Parked), target);
    }

    [Fact]
    public void A_parked_card_remembers_where_it_came_from()
    {
        CardPosition position = BoardWorkflow.PositionOf(Location.Parked, stepId: null, parkedFromStepId: Implement.Id);

        Assert.Equal(ParkedFrom(Implement), position);
    }

    [Fact]
    public void A_card_keeps_its_Track_position_even_if_the_step_no_longer_offers_Track() =>
        Assert.Equal(Track(Validate), BoardWorkflow.PositionOf(Location.Track, Validate.Id, parkedFromStepId: null));

    [Fact]
    public void A_card_in_a_step_the_workflow_lacks_is_a_data_error() =>
        Assert.Throws<InvalidOperationException>(
            () => BoardWorkflow.PositionOf(Location.Active, Guid.NewGuid(), parkedFromStepId: null));
}
