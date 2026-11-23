using KanbanHub.Domain.Flow;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

public class CardPositionTests
{
    [Theory]
    [InlineData(Location.Backlog)]
    [InlineData(Location.Parked)]
    [InlineData(Location.Canceled)]
    [InlineData(Location.Rollup)]
    public void A_position_in_a_step_needs_a_step_sub_column(Location location) =>
        Assert.Throws<ArgumentException>(() => CardPosition.InStep(Guid.NewGuid(), 1, location));

    [Fact]
    public void Step_order_starts_at_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CardPosition.InStep(Guid.NewGuid(), 0, Location.Active));

    [Theory]
    [InlineData(Location.Active)]
    [InlineData(Location.Done)]
    [InlineData(Location.Track)]
    public void A_step_sub_column_needs_a_step(Location location) =>
        Assert.Throws<ArgumentException>(() => CardPosition.OutsideSteps(location));

    [Fact]
    public void Positions_outside_steps_have_no_step()
    {
        CardPosition parked = ParkedFrom(Implement);

        Assert.Null(parked.StepId);
        Assert.Equal(0, parked.StepOrder);
        Assert.Equal(Implement.Id, parked.ParkedFromStepId);
    }

    [Fact]
    public void Pulling_into_an_earlier_step_is_rework()
    {
        Assert.True(Active(Validate).IsReworkedBy(Active(Implement)));
        Assert.True(Done(Implement).IsReworkedBy(Active(Specify)));
    }

    [Fact]
    public void Returning_from_Done_to_Active_in_the_same_step_is_rework() =>
        Assert.True(Done(Implement).IsReworkedBy(Active(Implement)));

    [Fact]
    public void Forward_and_sideways_moves_are_not_rework()
    {
        Assert.False(Done(Specify).IsReworkedBy(Active(Implement)));
        Assert.False(Active(Implement).IsReworkedBy(Done(Implement)));
        Assert.False(Active(Implement).IsReworkedBy(Active(Implement)));
        Assert.False(Track(Implement).IsReworkedBy(Active(Implement)));
        Assert.False(ParkedFrom(Implement).IsReworkedBy(Active(Implement)));
        Assert.False(CardPosition.Backlog.IsReworkedBy(Active(Specify)));
    }
}
