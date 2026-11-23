using KanbanHub.Domain.Flow;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

/// <summary>
/// Checks every (from, to) pair on a three-step board against the literal tables below,
/// with and without the right to move backward.
/// </summary>
public class TransitionsTests
{
    private const string AnywhereToParked = "Parked";

    private static readonly Dictionary<string, CardPosition> Origins = new()
    {
        ["Backlog"] = CardPosition.Backlog,
        ["Specify.Active"] = Active(Specify),
        ["Specify.Done"] = Done(Specify),
        ["Specify.Track"] = Track(Specify),
        ["Implement.Active"] = Active(Implement),
        ["Implement.Done"] = Done(Implement),
        ["Implement.Track"] = Track(Implement),
        ["Validate.Active"] = Active(Validate),
        ["Validate.Done"] = Done(Validate),
        ["Validate.Track"] = Track(Validate),
        ["Parked from backlog"] = ParkedFrom(null),
        ["Parked from Implement"] = ParkedFrom(Implement),
        ["Canceled"] = CardPosition.OutsideSteps(Location.Canceled),
        ["Rollup"] = CardPosition.OutsideSteps(Location.Rollup)
    };

    private static readonly Dictionary<string, CardPosition> Targets = new()
    {
        ["Backlog"] = CardPosition.Backlog,
        ["Specify.Active"] = Active(Specify),
        ["Specify.Done"] = Done(Specify),
        ["Specify.Track"] = Track(Specify),
        ["Implement.Active"] = Active(Implement),
        ["Implement.Done"] = Done(Implement),
        ["Implement.Track"] = Track(Implement),
        ["Validate.Active"] = Active(Validate),
        ["Validate.Done"] = Done(Validate),
        ["Validate.Track"] = Track(Validate),
        [AnywhereToParked] = CardPosition.OutsideSteps(Location.Parked),
        ["Canceled"] = CardPosition.OutsideSteps(Location.Canceled),
        ["Rollup"] = CardPosition.OutsideSteps(Location.Rollup)
    };

    /// <summary>FR-M01: legal for everyone who may move cards.</summary>
    private static readonly HashSet<(string From, string To)> ForwardMoves =
    [
        ("Backlog", "Specify.Active"),
        ("Specify.Active", "Specify.Done"),
        ("Implement.Active", "Implement.Done"),
        ("Validate.Active", "Validate.Done"),
        ("Specify.Done", "Implement.Active"),
        ("Implement.Done", "Validate.Active"),
        ("Specify.Active", "Specify.Track"),
        ("Specify.Track", "Specify.Active"),
        ("Implement.Active", "Implement.Track"),
        ("Implement.Track", "Implement.Active"),
        ("Validate.Active", "Validate.Track"),
        ("Validate.Track", "Validate.Active"),
        ("Parked from backlog", "Backlog"),
        ("Parked from Implement", "Backlog"),
        ("Parked from Implement", "Implement.Active")
    ];

    /// <summary>FR-M02: legal only with the right to move backward.</summary>
    private static readonly HashSet<(string From, string To)> BackwardMoves =
    [
        ("Specify.Active", "Specify.Active"),
        ("Specify.Done", "Specify.Active"),
        ("Implement.Active", "Specify.Active"),
        ("Implement.Active", "Implement.Active"),
        ("Implement.Done", "Specify.Active"),
        ("Implement.Done", "Implement.Active"),
        ("Validate.Active", "Specify.Active"),
        ("Validate.Active", "Implement.Active"),
        ("Validate.Active", "Validate.Active"),
        ("Validate.Done", "Specify.Active"),
        ("Validate.Done", "Implement.Active"),
        ("Validate.Done", "Validate.Active")
    ];

    public static TheoryData<string, string, bool> EveryPair()
    {
        var pairs = new TheoryData<string, string, bool>();
        foreach (string from in Origins.Keys)
            foreach (string to in Targets.Keys)
                foreach (bool canMoveBackward in (bool[])[false, true])
                    pairs.Add(from, to, canMoveBackward);
        return pairs;
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Every_pair_is_allowed_or_rejected_as_the_tables_say(string from, string to, bool canMoveBackward)
    {
        bool expectedLegal = to == AnywhereToParked
            || ForwardMoves.Contains((from, to))
            || (canMoveBackward && BackwardMoves.Contains((from, to)));

        string? reason = Transitions.Check(Origins[from], Targets[to], canMoveBackward);

        Assert.Equal(expectedLegal, reason is null);
    }

    [Fact]
    public void A_rejection_names_both_locations()
    {
        string? reason = Transitions.Check(CardPosition.Backlog, Done(Validate), canMoveBackward: true);

        Assert.Equal("A move from Backlog to Done isn't allowed on this board.", reason);
    }

    [Fact]
    public void A_parked_card_returns_only_to_the_step_it_came_from()
    {
        Assert.Null(Transitions.Check(ParkedFrom(Implement), Active(Implement), canMoveBackward: false));
        Assert.NotNull(Transitions.Check(ParkedFrom(Implement), Active(Validate), canMoveBackward: false));
        Assert.NotNull(Transitions.Check(ParkedFrom(null), Active(Specify), canMoveBackward: false));
    }

    [Fact]
    public void Skipping_a_step_is_never_allowed()
    {
        Assert.NotNull(Transitions.Check(Done(Specify), Active(Validate), canMoveBackward: true));
        Assert.NotNull(Transitions.Check(CardPosition.Backlog, Active(Implement), canMoveBackward: true));
    }
}
