using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Domain.Security;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Agents;

/// <summary>What comes back from GitHub after a card was sent to an agent (section 10.2, "Track" and "Finish").</summary>
public class AgentTrackingTests(KanbanDatabase database)
{
    [Fact]
    public async Task Merging_the_agents_pull_request_ends_the_session_frees_the_slot_and_leaves_the_move_to_people()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);
        await agents.PullRequestAsync(issue, "opened");

        await agents.PullRequestAsync(issue, "closed", AgentScenario.AgentPullRequest(issue) with { IsDraft = false, IsClosed = true, IsMerged = true });

        CardDetailDto shown = await agents.Board.CardAsync(card);
        AgentDeskDto desk = await agents.DeskAsync(agents.Board.Owner, card);
        Assert.Null(shown.Card.Agent);
        Assert.Equal(new PullRequestDto(77, PullRequestState.Merged, IsDraft: false, "contoso/checkout"), Assert.Single(shown.PullRequests));
        Assert.Equal((DispatchState.Merged, 77, 0), (desk.Latest?.State, desk.Latest?.PullRequestNumber, desk.OpenSessions));
        Assert.Equal((agents.Board.Implement.Id, Location.Active), (shown.Card.StepId, shown.Card.Location));   // moving stays a human action
    }

    [Fact]
    public async Task Closing_the_agents_pull_request_without_merging_ends_the_session_as_closed_and_the_card_can_be_sent_again()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);
        await agents.PullRequestAsync(issue, "opened");

        await agents.PullRequestAsync(issue, "closed", AgentScenario.AgentPullRequest(issue) with { IsClosed = true });
        IActionResult again = await agents.DispatchAsync(agents.Board.Owner, card);

        Assert.Equal(201, again.StatusOf());
        Assert.Equal(PullRequestState.Closed, Assert.Single((await agents.Board.CardAsync(card)).PullRequests).State);
        Assert.Equal(new CardAgentDto("copilot", DispatchState.Working, null), (await agents.Board.CardAsync(card)).Card.Agent);
    }

    [Fact]
    public async Task The_pull_request_shows_as_a_draft_until_the_agent_marks_it_ready()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);

        await agents.PullRequestAsync(issue, "opened");
        PullRequestDto draft = Assert.Single((await agents.Board.CardAsync(card)).PullRequests);
        await agents.PullRequestAsync(issue, "ready_for_review", AgentScenario.AgentPullRequest(issue) with { IsDraft = false });
        PullRequestDto ready = Assert.Single((await agents.Board.CardAsync(card)).PullRequests);

        Assert.Equal((PullRequestState.Open, true, "https://github.com/contoso/checkout/pull/77"), (draft.State, draft.IsDraft, draft.Url));
        Assert.Equal((PullRequestState.Open, false), (ready.State, ready.IsDraft));
    }

    /// <summary>A person's pull request for the same issue is shown on the card, but it isn't the agent's work.</summary>
    [Fact]
    public async Task A_pull_request_someone_else_opened_does_not_move_the_agents_session_on()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);

        await agents.PullRequestAsync(issue, "opened", new PullRequestFacts(81, "fff111", $"Closes #{issue.Reference.Number}") { Author = "octocat" });

        CardDetailDto shown = await agents.Board.CardAsync(card);
        Assert.Equal(new CardAgentDto("copilot", DispatchState.Working, null), shown.Card.Agent);
        Assert.Equal(81, Assert.Single(shown.PullRequests).Number);
    }

    [Fact]
    public async Task Once_the_session_has_its_pull_request_another_one_by_the_agent_does_not_replace_it()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);
        await agents.PullRequestAsync(issue, "opened");

        await agents.PullRequestAsync(issue, "closed", AgentScenario.AgentPullRequest(issue, number: 90) with { IsClosed = true });

        Assert.Equal(new CardAgentDto("copilot", DispatchState.InReview, 77), (await agents.Board.CardAsync(card)).Card.Agent);
    }

    /// <summary>Unassigning the agent is how people stop it on GitHub: its session ends here too.</summary>
    [Fact]
    public async Task Unassigning_the_agent_on_github_ends_a_working_session()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);

        await UnassignAsync(agents, issue, "octocat");
        DispatchState? afterAPerson = (await agents.DeskAsync(agents.Board.Owner, card)).Latest?.State;
        await UnassignAsync(agents, issue, "Copilot");
        AgentDeskDto afterTheAgent = await agents.DeskAsync(agents.Board.Owner, card);

        Assert.Equal(DispatchState.Working, afterAPerson);
        Assert.Equal((DispatchState.Ended, 0), (afterTheAgent.Latest?.State, afterTheAgent.OpenSessions));
        Assert.Null((await agents.Board.CardAsync(card)).Card.Agent);
    }

    [Fact]
    public async Task A_session_that_already_has_its_pull_request_outlives_the_unassignment()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);
        await agents.PullRequestAsync(issue, "opened");

        await UnassignAsync(agents, issue, "Copilot");

        Assert.Equal(DispatchState.InReview, (await agents.Board.CardAsync(card)).Card.Agent?.State);
    }

    /// <summary>The board is told when a session moves on, so the "Agent working" badge changes without a refresh.</summary>
    [Fact]
    public async Task Every_step_of_a_session_reaches_the_board_live()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.Board.PublishEventsOfAsync(card);
        int before = agents.Board.Gateway.BoardChanges.Count;

        await agents.DispatchAsync(agents.Board.Owner, card);
        await agents.Board.PublishEventsOfAsync(card);
        int afterDispatch = agents.Board.Gateway.BoardChanges.Count;
        await agents.PullRequestAsync(issue, "opened");
        await agents.Board.PublishEventsOfAsync(card);
        int afterPullRequest = agents.Board.Gateway.BoardChanges.Count;

        Assert.True(before < afterDispatch && afterDispatch < afterPullRequest,
            $"The board was told {before}, then {afterDispatch}, then {afterPullRequest} times.");
    }

    /// <summary>The done-rule check belongs to open pull requests; a merged one keeps the check it ended with.</summary>
    [Fact]
    public async Task The_agents_pull_request_gets_the_done_rule_check_while_it_is_open()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, StoredIssue issue) = await agents.ReadyCardAsync();
        await agents.DispatchAsync(agents.Board.Owner, card);

        await agents.PullRequestAsync(issue, "opened");
        int whileOpen = agents.GitHub.GitHub.CheckRuns.Count;
        await agents.PullRequestAsync(issue, "closed", AgentScenario.AgentPullRequest(issue) with { HeadSha = "merged999", IsClosed = true, IsMerged = true });

        Assert.Equal(1, whileOpen);
        Assert.Equal(whileOpen, agents.GitHub.GitHub.CheckRuns.Count);
    }

    private static async Task UnassignAsync(AgentScenario agents, StoredIssue issue, string assignee)
    {
        var payload = new
        {
            action = "unassigned",
            issue = new { number = issue.Reference.Number, title = issue.Title, state = "open" },
            assignee = new { login = assignee },
            repository = new { id = agents.GitHub.Repository.RepositoryId, full_name = agents.GitHub.Repository.FullName },
            installation = new { id = agents.GitHub.Repo.InstallationId },
            sender = new { login = "octocat" }
        };
        Assert.Equal(202, (await agents.GitHub.DeliverAsync("issues", payload)).StatusOf());
    }
}

/// <summary>"Connect GitHub" through the API (section 9.1).</summary>
public class GitHubConnectionEndpointTests(KanbanDatabase database)
{
    [Fact]
    public async Task Connecting_sends_the_person_to_github_and_the_callback_links_their_account()
    {
        BoardScenario board = await StartAsync(database);

        var before = (await board.Connection.Get(Requests.From(board.Owner), Ct)).ValueOf<GitHubConnectionDto>();
        var start = (await board.Connection.Connect(Requests.From(board.Owner), Ct)).ValueOf<ConnectGitHubResponse>();
        IActionResult completed = await board.Connection.Complete(Requests.WithQuery(board.Owner, new Uri(start.AuthorizeUrl).Query), Ct);
        var after = (await board.Connection.Get(Requests.From(board.Owner), Ct)).ValueOf<GitHubConnectionDto>();

        Assert.Equal((false, null), (before.IsConnected, before.Login));
        Assert.StartsWith("https://kanbanhub.test/github/callback?code=", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(new GitHubConnectionDto("octocat"), completed.ValueOf<GitHubConnectionDto>());
        Assert.Equal((true, "octocat"), (after.IsConnected, after.Login));
    }

    /// <summary>The state ties GitHub's answer to the person who started: nobody can connect someone else's session.</summary>
    [Fact]
    public async Task The_callback_counts_once_for_the_person_who_started_and_never_for_anyone_else()
    {
        BoardScenario board = await StartAsync(database);
        var intruder = TestUsers.WithTenantRoles();
        var start = (await board.Connection.Connect(Requests.From(board.Owner), Ct)).ValueOf<ConnectGitHubResponse>();
        string query = new Uri(start.AuthorizeUrl).Query;

        IActionResult asIntruder = await board.Connection.Complete(Requests.WithQuery(intruder, query), Ct);
        IActionResult asOwner = await board.Connection.Complete(Requests.WithQuery(board.Owner, query), Ct);
        IActionResult replayed = await board.Connection.Complete(Requests.WithQuery(board.Owner, query), Ct);

        asIntruder.ProblemOf(422, "validation-failed");
        Assert.Equal(200, asOwner.StatusOf());
        replayed.ProblemOf(422, "validation-failed");
        Assert.False((await board.Connection.Get(Requests.From(intruder), Ct)).ValueOf<GitHubConnectionDto>().IsConnected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?code=local-code")]
    [InlineData("?state=0000")]
    [InlineData("?code=local-code&state=made-up")]
    [InlineData("?error=access_denied&state=0000")]
    public async Task A_callback_without_a_code_and_a_state_that_was_handed_out_connects_nothing(string query)
    {
        BoardScenario board = await StartAsync(database);

        IActionResult completed = await board.Connection.Complete(Requests.WithQuery(board.Owner, query), Ct);

        completed.ProblemOf(422, "validation-failed");
    }

    [Fact]
    public async Task Disconnecting_forgets_the_account_and_stops_dispatching_at_once()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);
        (Guid card, _) = await agents.ReadyCardAsync();

        IActionResult disconnected = await agents.Board.Connection.Disconnect(Requests.From(agents.Board.Owner), Ct);
        IActionResult again = await agents.Board.Connection.Disconnect(Requests.From(agents.Board.Owner), Ct);
        IActionResult sent = await agents.DispatchAsync(agents.Board.Owner, card);

        Assert.Equal(204, disconnected.StatusOf());
        again.ProblemOf(404, "not-found");
        Assert.Equal(AgentLine.ConnectFirst, sent.ProblemOf(422, "validation-failed").Detail);
        Assert.Empty(agents.Dispatcher.Sent);
    }

    /// <summary>Only the protected refresh token is stored; the access token lives in memory.</summary>
    [Fact]
    public async Task The_database_keeps_the_refresh_token_only_as_the_protector_returned_it()
    {
        AgentScenario agents = await AgentScenario.StartAsync(database);

        var accounts = agents.Board.Service<IGitHubAccountStore>();
        byte[]? stored = await accounts.FindRefreshTokenAsync(agents.Board.Owner.GetObjectId(), Ct);

        Assert.Equal("local-refresh-1", System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(stored)));
    }
}
