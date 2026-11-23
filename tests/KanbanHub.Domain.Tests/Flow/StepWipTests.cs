using CsCheck;
using KanbanHub.Domain.Flow;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

public class StepWipTests
{
    private static readonly WipLimit LimitOfFive = new(5);

    [Fact]
    public void Active_and_Done_count_but_Track_does_not()
    {
        var wip = new StepWip(Implement, [.. Cards(3, Location.Active), .. Cards(2, Location.Done), .. Cards(4, Location.Track)]);

        Assert.Equal(5, wip.Count);
    }

    [Fact]
    public void The_final_steps_Done_never_counts()
    {
        var wip = new StepWip(Validate, [.. Cards(2, Location.Active), .. Cards(40, Location.Done)]);

        Assert.Equal(2, wip.Count);
    }

    [Fact]
    public void Outside_the_breakdown_step_every_child_counts_on_its_own()
    {
        Guid parent = Guid.NewGuid();

        var wip = new StepWip(Implement, [Child(parent), Child(parent), Child(parent)]);

        Assert.Equal(3, wip.Count);
    }

    [Fact]
    public void In_the_breakdown_step_a_parent_and_its_children_count_as_one()
    {
        var parentP = Card(Location.Done);
        var parentQ = Card(Location.Active);
        StepOccupant[] childrenOfP = [.. Enumerable.Range(0, 4).Select(_ => Child(parentP.CardId, Location.Done))];

        var wip = new StepWip(Specify, [parentP, parentQ, .. childrenOfP]);

        Assert.Equal(2, wip.Count);
    }

    [Fact]
    public void A_child_joining_its_family_in_the_breakdown_step_adds_nothing()
    {
        var parent = Card(Location.Active);
        var wip = new StepWip(Specify, [parent]);

        Assert.Equal(1, wip.CountAfterPulling(Child(parent.CardId, Location.Backlog)));
        Assert.Equal(2, wip.CountAfterPulling(Card(Location.Backlog)));
    }

    [Fact]
    public void A_card_already_counted_in_the_step_is_not_counted_twice()
    {
        var finishing = Card(Location.Done);
        var wip = new StepWip(Implement, [finishing, .. Cards(4, Location.Active)]);

        Assert.Equal(5, wip.CountAfterPulling(finishing));
        Assert.Null(wip.FindViolation(LimitOfFive, finishing));
    }

    [Fact]
    public void A_pull_that_fits_violates_nothing()
    {
        var wip = new StepWip(Implement, Cards(4, Location.Active));

        Assert.Null(wip.FindViolation(LimitOfFive, Card(Location.Done)));
    }

    [Fact]
    public void A_pull_into_a_full_step_reports_the_limit_and_the_count_before_the_move()
    {
        var wip = new StepWip(Implement, [.. Cards(3, Location.Active), .. Cards(2, Location.Done)]);

        WipViolation? violation = wip.FindViolation(LimitOfFive, Card(Location.Done));

        Assert.Equal(new WipViolation(WipScope.Step, "Implement", Limit: 5, Current: 5, WipPolicy.Strict), violation);
    }

    [Fact]
    public void A_limit_lowered_below_the_count_accepts_no_pull_until_the_step_drains()
    {
        var lowered = new WipLimit(3, WipPolicy.Warn);
        var wip = new StepWip(Implement, Cards(5, Location.Active));

        WipViolation? violation = wip.FindViolation(lowered, Card(Location.Done));

        Assert.Equal(new WipViolation(WipScope.Step, "Implement", Limit: 3, Current: 5, WipPolicy.Warn), violation);
    }

    [Fact]
    public void Grouping_families_never_raises_the_count() =>
        Occupants.Sample(occupants =>
        {
            int grouped = new StepWip(Specify, occupants).Count;
            int ungrouped = new StepWip(Implement, occupants).Count;
            return grouped <= ungrouped && ungrouped <= occupants.Length;
        });

    [Fact]
    public void A_pull_adds_one_unit_or_none() =>
        Occupants.Sample(occupants =>
        {
            var wip = new StepWip(Specify, occupants);
            int added = wip.CountAfterPulling(Child(Families[0], Location.Backlog)) - wip.Count;
            return added is 0 or 1;
        });

    private static readonly Guid[] Families = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private static readonly Gen<StepOccupant[]> Occupants =
        Gen.Select(Gen.Guid, Gen.Int[0, Families.Length], Gen.Int[0, (int)Location.Rollup],
                (cardId, family, location) => new StepOccupant(cardId, ParentOf(family), (Location)location))
            .Array[0, 30];

    private static Guid? ParentOf(int family) => family == 0 ? null : Families[family - 1];

    private static StepOccupant Card(Location location) => new(Guid.NewGuid(), ParentCardId: null, location);

    private static StepOccupant Child(Guid parentId, Location location = Location.Active) =>
        new(Guid.NewGuid(), parentId, location);

    private static StepOccupant[] Cards(int count, Location location) =>
        [.. Enumerable.Range(0, count).Select(_ => Card(location))];
}
