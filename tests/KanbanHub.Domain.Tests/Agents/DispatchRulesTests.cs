using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;

namespace KanbanHub.Domain.Tests.Agents;

/// <summary>A three-step board whose Implement step is open to agents, and a linked card on it.</summary>
internal static class AgentBoard
{
    public static readonly SyncedStep Specify = new(Guid.NewGuid(), "Specify", 1);
    public static readonly SyncedStep Implement = new(Guid.NewGuid(), "Implement", 2) { HasBlockedColumn = true, IsAgentEligible = true };
    public static readonly SyncedStep Validate = new(Guid.NewGuid(), "Validate", 3) { IsFinal = true };

    public static readonly RepoLink Repository = new(Guid.NewGuid(), new RepoRef(1, 1296269), "contoso/checkout", SyncMode.OnDemand, LabelFilter: null);

    public static SyncedCard Card(SyncedStep? step, Location location) => new()
    {
        CardId = Guid.NewGuid(),
        BoardId = Repository.BoardId,
        Number = 12,
        RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        Content = new CardContent { Title = "Checkout fails for guests", Description = "Guests can't pay." },
        Location = location,
        StepId = step?.Id,
        Steps = [Specify, Implement, Validate]
    };

    /// <summary>A card linked to issue #42, on a board with one of three agent slots taken.</summary>
    public static DispatchContext Context(SyncedCard card) => new(
        card, new IssueLink(card.CardId, new IssueRef(1, 1296269, 42)), Repository, new AgentCapacity(Open: 1, Limit: 3), Latest: null);

    public static AgentSession Session(Guid cardId, DispatchState state) => new()
    {
        Id = Guid.NewGuid(),
        CardId = cardId,
        Agent = AgentCatalog.Copilot.Key,
        TargetRepository = Repository.FullName,
        BaseBranch = "main",
        State = state,
        DispatchedBy = Guid.NewGuid(),
        DispatchedAt = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero)
    };
}

public class PlacementTests
{
    [Fact]
    public void A_card_in_active_of_an_agent_step_is_in_place()
    {
        Assert.IsType<InPlace>(DispatchRules.PlacementOf(AgentBoard.Card(AgentBoard.Implement, Location.Active)).Value);
    }

    [Fact]
    public void A_card_waiting_in_done_of_the_step_before_is_pulled_into_active_first()
    {
        Placement placement = DispatchRules.PlacementOf(AgentBoard.Card(AgentBoard.Specify, Location.Done));

        NeedsPull pull = Assert.IsType<NeedsPull>(placement.Value);
        Assert.Equal((AgentBoard.Implement.Id, Location.Active), (pull.Target.StepId, pull.Target.Location));
    }

    [Fact]
    public void A_card_set_aside_in_blocked_must_come_back_first()
    {
        Placement placement = DispatchRules.PlacementOf(AgentBoard.Card(AgentBoard.Implement, Location.Track));

        Assert.Contains("Blocked", Assert.IsType<NotEligible>(placement.Value).Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Location.Backlog)]
    [InlineData(Location.Parked)]
    public void A_card_outside_the_workflow_is_not_eligible(Location location)
    {
        Placement placement = DispatchRules.PlacementOf(AgentBoard.Card(step: null, location));

        Assert.Contains("Agents work in Implement", Assert.IsType<NotEligible>(placement.Value).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_still_being_worked_on_in_the_step_before_is_not_pulled()
    {
        Assert.IsType<NotEligible>(DispatchRules.PlacementOf(AgentBoard.Card(AgentBoard.Specify, Location.Active)).Value);
    }

    [Fact]
    public void A_card_already_done_in_the_agent_step_is_not_eligible()
    {
        Placement placement = DispatchRules.PlacementOf(AgentBoard.Card(AgentBoard.Implement, Location.Done));

        Assert.Equal("The card is already done in Implement.", Assert.IsType<NotEligible>(placement.Value).Why);
    }

    [Fact]
    public void A_board_without_an_agent_step_says_where_to_set_one()
    {
        SyncedCard card = AgentBoard.Card(AgentBoard.Specify, Location.Active) with { Steps = [AgentBoard.Specify, AgentBoard.Validate] };

        Assert.Contains("Settings, Workflow", Assert.IsType<NotEligible>(DispatchRules.PlacementOf(card).Value).Why, StringComparison.Ordinal);
    }
}

public class DispatchObstacleTests
{
    private static readonly SyncedCard Card = AgentBoard.Card(AgentBoard.Implement, Location.Active);

    [Fact]
    public void A_linked_card_in_place_with_a_free_slot_has_no_obstacle()
    {
        Assert.Null(DispatchRules.FindObstacle(AgentBoard.Context(Card)));
    }

    [Fact]
    public void A_card_without_an_issue_must_be_linked_first()
    {
        DispatchContext context = AgentBoard.Context(Card) with { Link = null, Repository = null };

        Assert.Equal("Link the card to a GitHub issue first.", DispatchRules.FindObstacle(context));
    }

    [Theory]
    [InlineData(DispatchState.Working)]
    [InlineData(DispatchState.InReview)]
    public void A_card_with_an_open_session_is_busy(DispatchState open)
    {
        DispatchContext context = AgentBoard.Context(Card) with { Latest = AgentBoard.Session(Card.CardId, open) };

        Assert.Equal(DispatchRules.CardBusy, DispatchRules.FindObstacle(context));
    }

    [Theory]
    [InlineData(DispatchState.Merged)]
    [InlineData(DispatchState.Closed)]
    [InlineData(DispatchState.Failed)]
    [InlineData(DispatchState.Ended)]
    public void A_session_that_is_over_does_not_stand_in_the_way(DispatchState over)
    {
        DispatchContext context = AgentBoard.Context(Card) with { Latest = AgentBoard.Session(Card.CardId, over) };

        Assert.Null(DispatchRules.FindObstacle(context));
    }

    [Fact]
    public void A_board_at_its_agent_limit_says_how_many_are_open()
    {
        DispatchContext context = AgentBoard.Context(Card) with { Capacity = new AgentCapacity(Open: 3, Limit: 3) };

        Assert.Equal("This board has 3 agent sessions open and allows 3. Review one of them first.", DispatchRules.FindObstacle(context));
    }

    [Fact]
    public void A_limit_of_zero_keeps_agents_off_the_board()
    {
        Assert.True(new AgentCapacity(Open: 0, Limit: 0).IsFull);
    }
}

public class DispatchRequestTests
{
    private static readonly DispatchContext Context = AgentBoard.Context(AgentBoard.Card(AgentBoard.Implement, Location.Active));

    private static DispatchCommand Command(string agent = "copilot", string branch = "main", string? instructions = null) =>
        new(Context.Card.CardId, agent, branch, instructions);

    [Theory]
    [InlineData("main")]
    [InlineData("release/2026.11")]
    [InlineData("feature/guest-checkout_v2")]
    public void A_known_agent_and_a_plain_branch_name_pass(string branch)
    {
        Assert.Null(DispatchRules.FindProblem(Command(branch: branch), Context));
    }

    [Fact]
    public void An_agent_that_is_not_in_the_catalog_is_refused()
    {
        Assert.Equal("Unknown agent.", DispatchRules.FindProblem(Command(agent: "skynet"), Context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("main; rm -rf /")]
    [InlineData("two words")]
    public void A_branch_name_with_anything_odd_in_it_is_refused(string branch)
    {
        Assert.StartsWith("Name the base branch", DispatchRules.FindProblem(Command(branch: branch), Context), StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_have_a_length_limit()
    {
        string tooLong = new('x', AgentInstructions.MaxTeamInstructionsLength + 1);

        Assert.StartsWith("The instructions can be at most", DispatchRules.FindProblem(Command(instructions: tooLong), Context), StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_in_the_instructions_blocks_the_dispatch()
    {
        string? problem = DispatchRules.FindProblem(Command(instructions: "Deploy with password = hunter2hunter2"), Context);

        Assert.Contains("a password or API key", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_in_the_card_itself_blocks_the_dispatch_too()
    {
        SyncedCard leaky = Context.Card with
        {
            Content = Context.Card.Content with { Description = "Use " + "ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789 to call the API." }
        };

        Assert.Contains("a GitHub token", DispatchRules.FindProblem(Command(), Context with { Card = leaky }), StringComparison.Ordinal);
    }
}

public class AgentCatalogTests
{
    [Theory]
    [InlineData("copilot-swe-agent[bot]")]
    [InlineData("copilot-swe-agent")]
    [InlineData("Copilot")]
    [InlineData("COPILOT")]
    public void Copilot_answers_to_every_login_github_shows_it_under(string login)
    {
        Assert.True(AgentCatalog.Copilot.Answers(login));
    }

    [Fact]
    public void A_person_is_not_an_agent()
    {
        Assert.False(AgentCatalog.Copilot.Answers("octocat"));
        Assert.Null(AgentCatalog.Find("octocat"));
        Assert.Same(AgentCatalog.Copilot, AgentCatalog.Find("Copilot"));
    }

    [Theory]
    [InlineData(DispatchState.Working, true)]
    [InlineData(DispatchState.InReview, true)]
    [InlineData(DispatchState.Merged, false)]
    [InlineData(DispatchState.Closed, false)]
    [InlineData(DispatchState.Failed, false)]
    [InlineData(DispatchState.Ended, false)]
    public void Only_working_and_in_review_sessions_hold_a_slot(DispatchState state, bool holdsSlot)
    {
        Assert.Equal(holdsSlot, state.IsOpen());
    }
}
