using KanbanHub.Contracts;
using KanbanHub.Domain.Flow;
using KanbanHub.Web.State;
using KanbanHub.Web.Tests.Infrastructure;
using static KanbanHub.Web.Tests.Infrastructure.SampleBoard;

namespace KanbanHub.Web.Tests;

/// <summary>Standup mode: the walk from right to left, what comes first at each stop, and the 15 minutes.</summary>
public class StandupTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_walk_goes_from_the_last_step_to_the_first()
    {
        IReadOnlyList<StandupStop> stops = StandupWalk.Plan(With(), Now);

        Assert.Equal(["Validate", "Implement", "Specify"], stops.Select(stop => stop.Step.Name));
    }

    [Fact]
    public void At_a_stop_blocked_cards_come_first_and_then_whatever_waited_longest()
    {
        CardDto fresh = Placed("Fresh", Implement, Location.Active, daysAgo: 0);
        CardDto old = Placed("Old", Implement, Location.Done, daysAgo: 5);
        CardDto blocked = Placed("Blocked", Implement, Location.Track, daysAgo: 1) with { IsBlocked = true };

        StandupStop stop = StandupWalk.Plan(With(fresh, old, blocked), Now).Single(visited => visited.Step.Id == Implement.Id);

        Assert.Equal(["Blocked", "Old", "Fresh"], stop.Cards.Select(card => card.Card.Title));
        Assert.Equal([1, 5, 0], stop.Cards.Select(card => card.DaysInPlace));
        Assert.Equal([false, true, false], stop.Cards.Select(card => card.IsAging));
        Assert.Equal([true, true, false], stop.Cards.Select(card => card.NeedsAttention));
        Assert.Equal(1, stop.BlockedCount);
    }

    [Fact]
    public void Finished_work_and_cards_outside_the_steps_are_not_discussed()
    {
        CardDto finished = Placed("Finished", Validate, Location.Done, daysAgo: 9);
        CardDto validating = Placed("Validating", Validate, Location.Active, daysAgo: 0);
        CardDto idea = Placed("Idea", step: null, Location.Backlog, daysAgo: 30);
        CardDto parked = Placed("Parked", step: null, Location.Parked, daysAgo: 30);

        IReadOnlyList<StandupStop> stops = StandupWalk.Plan(With(finished, validating, idea, parked), Now);

        Assert.Equal(["Validating"], stops.SelectMany(stop => stop.Cards).Select(card => card.Card.Title));
    }

    [Fact]
    public void A_stop_shows_how_much_room_its_step_has()
    {
        Assert.Equal(1, new StandupStop(Implement with { WipCount = 1 }, []).Room);
        Assert.Equal(0, new StandupStop(Implement with { WipCount = 3 }, []).Room);
        Assert.Null(new StandupStop(Specify with { WipCount = 4 }, []).Room);
    }

    [Fact]
    public void The_closing_question_lists_every_blocked_card_the_longest_wait_first()
    {
        CardDto stuck = Placed("Stuck", Implement, Location.Track, daysAgo: 4) with { IsBlocked = true };
        CardDto justBlocked = Placed("Just blocked", Specify, Location.Active, daysAgo: 0) with { IsBlocked = true };
        CardDto flowing = Placed("Flowing", Implement, Location.Active, daysAgo: 7);

        IReadOnlyList<StandupCard> blocked = StandupWalk.Blocked(With(justBlocked, flowing, stuck), Now);

        Assert.Equal(["Stuck", "Just blocked"], blocked.Select(card => card.Card.Title));
    }

    [Fact]
    public void The_clock_counts_down_fifteen_minutes_and_then_shows_the_overrun()
    {
        var time = new AdjustableTime(Now);
        var clock = new StandupClock(time);
        (string Display, bool IsRunning) before = (clock.Display, clock.IsRunning);

        clock.Start();
        time.Advance(TimeSpan.FromSeconds(61));
        (string Display, bool IsOver) underway = (clock.Display, clock.IsOver);
        time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(9));
        (string Display, bool IsOver) late = (clock.Display, clock.IsOver);
        clock.Reset();

        Assert.Equal(("15:00", false), before);
        Assert.Equal(("13:59", false), underway);
        Assert.Equal(("+01:10", true), late);
        Assert.Equal(("15:00", false), (clock.Display, clock.IsRunning));
    }

    private static CardDto Placed(string title, StepDto? step, Location location, int daysAgo) =>
        Card(title, step, location) with { EnteredLocationAtUtc = Now.AddDays(-daysAgo).AddHours(-1) };
}
