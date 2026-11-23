using KanbanHub.Domain.Ai;

namespace KanbanHub.Domain.Tests.Ai;

/// <summary>A small board as the AI features see it: one card finished, one blocked, one aging, two waiting for Implement.</summary>
internal static class Flow
{
    public static readonly StepFlow Specify = new(Guid.NewGuid(), "Specify", 1) { DoneCount = 2, OldestDoneDays = 3, FinishedLast28Days = 9 };
    public static readonly StepFlow Implement = new(Guid.NewGuid(), "Implement", 2) { ActiveCount = 4, BlockedCount = 1, WipLimit = 5, OverridesLast28Days = 2 };
    public static readonly StepFlow Validate = new(Guid.NewGuid(), "Validate", 3) { IsFinal = true, ActiveCount = 1, DoneCount = 1, WipLimit = 3 };

    public static readonly FlowSnapshot Board = new()
    {
        Steps = [Specify, Implement, Validate],
        Finished = [new FlowCard(1, "Guest checkout", "Validate", 0)],
        Blocked = [new FlowCard(5, "Tax service", "Implement", 4) { Reason = "Waiting for the vendor" }],
        Aging = [new FlowCard(6, "Saved cards", "Implement", 6)],
        Waiting = [new FlowCard(7, "Promo codes", "Specify", 3), new FlowCard(8, "Gift receipts", "Specify", 1)]
    };
}

public class GroundingTests
{
    [Theory]
    [InlineData("Card #5 and #7 need help.", true)]
    [InlineData("Card #5 blocks #99.", false)]
    [InlineData("Nothing is cited here.", true)]
    [InlineData(null, true)]
    public void Free_text_may_mention_only_cards_the_data_holds(string? text, bool expected)
    {
        Assert.Equal(expected, Grounding.MentionsOnly(text, [5, 6, 7]));
    }

    [Theory]
    [InlineData("4 cards finished and 2 wait.", true)]
    [InlineData("About 2.7 tasks a day.", true)]            // 2.667 was supplied: rounding is not inventing
    [InlineData("About 2.67 tasks a day.", true)]
    [InlineData("About 2.8 tasks a day.", false)]
    [InlineData("17 cards finished.", false)]
    [InlineData("P0 cards past the 85th percentile, see #17.", true)]   // labels and card references aren't quantities
    [InlineData("Finished 4.", true)]
    [InlineData("", true)]
    public void A_stated_number_must_be_one_the_data_supplied(string text, bool expected)
    {
        Assert.Equal(expected, Grounding.NumbersComeFrom(text, [4, 2, 2.667m]));
    }

    [Fact]
    public void Card_references_are_read_from_text()
    {
        Assert.Equal([12, 7], Grounding.CardsIn("See #12, then #7 (not issue 9)."));
    }

    [Theory]
    [InlineData(" implement ", true)]
    [InlineData("Deploy", false)]
    [InlineData(null, false)]
    public void A_name_must_be_one_of_the_known_names(string? name, bool expected)
    {
        Assert.Equal(expected, Grounding.IsOneOf(name, ["Specify", "Implement"]));
    }
}

public class StandupBriefTaskTests
{
    private static readonly StandupBriefTask Task = new();

    private static StandupBrief Brief(
        int[]? finished = null, int[]? blocked = null, int[]? atRisk = null, string swarm = "Implement", string summary = "1 finished, 1 blocked.") =>
        new(finished ?? [1], blocked ?? [5], atRisk ?? [6, 5], [new SwarmSuggestion(swarm, "4 cards are in progress there.")], summary);

    [Fact]
    public void A_brief_that_cites_only_the_boards_cards_steps_and_numbers_is_grounded()
    {
        Assert.True(Task.IsGrounded(Flow.Board, Brief()));
    }

    [Fact]
    public void Each_list_may_cite_only_cards_from_the_list_it_summarizes()
    {
        Assert.False(Task.IsGrounded(Flow.Board, Brief(finished: [5])));        // blocked, not finished
        Assert.False(Task.IsGrounded(Flow.Board, Brief(blocked: [6])));         // aging, not blocked
        Assert.False(Task.IsGrounded(Flow.Board, Brief(atRisk: [1])));          // finished cards aren't at risk
        Assert.False(Task.IsGrounded(Flow.Board, Brief(finished: [1, 99])));    // no such card
    }

    [Theory]
    [InlineData("Deploy", "1 finished, 1 blocked.")]
    [InlineData("Implement", "12 finished since yesterday.")]
    [InlineData("Implement", "Card #99 is on fire.")]
    [InlineData("Implement", "")]
    public void A_made_up_step_number_or_card_fails_the_check(string swarm, string summary)
    {
        Assert.False(Task.IsGrounded(Flow.Board, Brief(swarm: swarm, summary: summary)));
    }

    [Fact]
    public void The_insight_takes_titles_and_reasons_from_the_board_not_from_the_model()
    {
        Insight insight = StandupBriefTask.ToInsight(Flow.Board, Brief());

        Assert.Equal(
            [
                "Finished: #1 Guest checkout",
                "Blocked for 4 days: #5 Tax service (Waiting for the vendor)",
                "At risk: #6 Saved cards, 6 days in Implement",
                "At risk: #5 Tax service, 4 days in Implement",
                "Swarm on Implement: 4 cards are in progress there."
            ],
            insight.Points);
        Assert.Equal([1, 5, 6], insight.Cards);
        Assert.Equal((AiFeature.StandupBrief, "Standup brief", "1 finished, 1 blocked."), (insight.Kind, insight.Title, insight.Summary));
    }
}

public class ConstraintFinderTests
{
    [Fact]
    public void Cards_piling_up_in_a_steps_done_make_the_next_step_the_constraint()
    {
        Constraint? found = ConstraintFinder.Find(Flow.Board);

        Assert.Equal(("Implement", "Specify"), (found?.ConstraintStep.Name, found?.WaitingStep.Name));
        Assert.Equal([7, 8], found?.WaitingCards.Select(card => card.Number));
    }

    [Fact]
    public void One_card_that_arrived_today_is_not_a_pile()
    {
        FlowSnapshot calm = Flow.Board with { Steps = [Flow.Specify with { DoneCount = 1, OldestDoneDays = 0 }, Flow.Implement, Flow.Validate] };

        Assert.Null(ConstraintFinder.Find(calm));
    }

    [Fact]
    public void One_card_waiting_for_days_is_a_sign_too()
    {
        FlowSnapshot stuck = Flow.Board with { Steps = [Flow.Specify with { DoneCount = 1, OldestDoneDays = 5 }, Flow.Implement, Flow.Validate] };

        Assert.Equal("Implement", ConstraintFinder.Find(stuck)?.ConstraintStep.Name);
    }

    /// <summary>Finished cards collect in the last step's Done; that is the board working, not a constraint.</summary>
    [Fact]
    public void The_last_steps_done_never_counts()
    {
        FlowSnapshot shipping = Flow.Board with
        {
            Steps = [Flow.Specify with { DoneCount = 0, OldestDoneDays = 0 }, Flow.Implement, Flow.Validate with { DoneCount = 40, OldestDoneDays = 12 }]
        };

        Assert.Null(ConstraintFinder.Find(shipping));
    }

    [Fact]
    public void A_statement_must_name_the_constraint_and_use_only_its_numbers()
    {
        var task = new BottleneckTask();
        Constraint found = ConstraintFinder.Find(Flow.Board)!;

        Assert.True(task.IsGrounded(found, new("Implement is the constraint: Specify Done holds 2 cards, the oldest for 3 days.", "Swarm on Implement.")));
        Assert.False(task.IsGrounded(found, new("Validate is slow: Specify Done holds 2 cards.", "Swarm on Validate.")));
        Assert.False(task.IsGrounded(found, new("Implement is the constraint: 14 cards wait.", "Swarm on Implement.")));
        Assert.False(task.IsGrounded(found, new("Implement is the constraint, see #1.", "Swarm on Implement.")));   // #1 isn't one of the waiting cards
    }

    [Fact]
    public void The_insight_is_titled_with_the_constraint_and_cites_the_waiting_cards()
    {
        Insight insight = BottleneckTask.ToInsight(ConstraintFinder.Find(Flow.Board)!, new("Implement is the constraint.", "Swarm on Implement."));

        Assert.Equal(("Implement is the constraint", "Implement is the constraint."), (insight.Title, insight.Summary));
        Assert.Equal(["Swarm on Implement."], insight.Points);
        Assert.Equal([7, 8], insight.Cards);
    }
}

public class WipAdviceTaskTests
{
    private static readonly WipAdviceTask Task = new();

    private static WipAdvice Advice(params WipSuggestionLine[] proposals) => new(proposals);

    private static WipSuggestionLine Line(string step, int current, int proposed, string reason) => new(step, current, proposed, reason);

    [Fact]
    public void A_change_of_one_to_a_limit_the_board_has_is_grounded()
    {
        Assert.True(Task.IsGrounded(Flow.Board, Advice(Line("Implement", 5, 6, "The limit was overridden 2 times in the last four weeks."))));
        Assert.True(Task.IsGrounded(Flow.Board, Advice(Line("validate", 3, 2, "Validate is faster than Implement."))));
        Assert.True(Task.IsGrounded(Flow.Board, Advice()));   // "the limits fit" is an answer too
    }

    [Theory]
    [InlineData("Implement", 5, 7, "Raise it.")]                       // two at once
    [InlineData("Implement", 4, 5, "Raise it.")]                       // not the current limit
    [InlineData("Specify", 2, 3, "Raise it.")]                         // Specify has no limit to tune
    [InlineData("Deploy", 5, 6, "Raise it.")]                          // no such step
    [InlineData("Implement", 5, 6, "It was overridden 17 times.")]     // a number the data doesn't hold
    [InlineData("Implement", 5, 6, "")]
    public void Anything_else_fails_the_check(string step, int current, int proposed, string reason)
    {
        Assert.False(Task.IsGrounded(Flow.Board, Advice(Line(step, current, proposed, reason))));
    }

    [Fact]
    public void A_limit_never_drops_below_one_and_a_step_gets_one_proposal()
    {
        FlowSnapshot tight = Flow.Board with { Steps = [Flow.Specify, Flow.Implement with { WipLimit = 1 }, Flow.Validate] };

        Assert.False(Task.IsGrounded(tight, Advice(Line("Implement", 1, 0, "Lower it."))));
        Assert.False(Task.IsGrounded(Flow.Board, Advice(Line("Implement", 5, 6, "Raise it."), Line("implement", 5, 4, "Lower it."))));
    }

    [Fact]
    public void Each_proposal_becomes_an_insight_that_carries_the_change_to_accept()
    {
        Insight insight = Assert.Single(WipAdviceTask.ToInsights(Flow.Board, Advice(Line("implement", 5, 6, "Overridden 2 times."))));

        Assert.Equal(("Raise Implement from 5 to 6", "Overridden 2 times."), (insight.Title, insight.Summary));
        Assert.Equal(new WipProposal(Flow.Implement.StepId, "Implement", 5, 6), insight.Proposal);
    }
}

public class ForecastNarrativeTaskTests
{
    private static readonly ForecastNarrativeTask Task = new();
    private static readonly ForecastFacts Facts = new(14, 1.5m, 0.75m, 12, 16m, NeverFinishes: false);

    [Theory]
    [InlineData("Over 14 days the team finished 1.5 tasks a day while 0.75 arrived; 12 remain, about 16 days.", true)]
    [InlineData("Over 14 days the team finished 1.5 tasks a day while 0.8 arrived.", true)]    // 0.75 rounds to 0.8
    [InlineData("The team will be done in 9 days.", false)]
    [InlineData("See card #3 for details.", false)]
    [InlineData("", false)]
    public void The_narrative_states_the_forecasts_numbers_and_no_others(string summary, bool expected)
    {
        Assert.Equal(expected, Task.IsGrounded(Facts, new ForecastNarrative(summary)));
    }

    [Fact]
    public void Facts_are_the_forecast_rounded_to_cents()
    {
        var forecast = new KanbanHub.Domain.Planning.Forecast(CompletionPerDay: 1.0 / 3, AddPerDay: 0.125, CurrentTaskEstimate: 7, DaysRemaining: null);

        ForecastFacts facts = ForecastFacts.Of(forecast, windowDays: 14);

        Assert.Equal((14, 0.33m, 0.13m, 7, null, true),
            (facts.WindowDays, facts.FinishedPerDay, facts.AddedPerDay, facts.TasksRemaining, facts.DaysRemaining, facts.NeverFinishes));
    }
}
