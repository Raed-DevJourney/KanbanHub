using KanbanHub.Contracts;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.Web.Api;
using KanbanHub.Web.State;
using KanbanHub.Web.Tests.Infrastructure;
using static KanbanHub.Web.Tests.Infrastructure.SampleBoard;

namespace KanbanHub.Web.Tests;

/// <summary>What the app says about agent sessions and pull requests, and how it talks to the agent endpoints.</summary>
public class AgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_card_says_agent_working_until_its_pull_request_waits_for_review()
    {
        Assert.Equal("Agent working", AgentLabels.BadgeOf(new CardAgentDto("copilot", DispatchState.Working, null)));
        Assert.Equal("Agent PR #77", AgentLabels.BadgeOf(new CardAgentDto("copilot", DispatchState.InReview, 77)));
        Assert.Equal("GitHub Copilot", AgentLabels.NameOf("copilot"));
        Assert.Equal("some-new-agent", AgentLabels.NameOf("some-new-agent"));
    }

    [Theory]
    [InlineData(DispatchState.Working, "GitHub Copilot is working on this card, from branch main.")]
    [InlineData(DispatchState.InReview, "GitHub Copilot opened pull request #77. It waits for review.")]
    [InlineData(DispatchState.Merged, "GitHub Copilot's pull request #77 was merged.")]
    [InlineData(DispatchState.Closed, "GitHub Copilot's pull request #77 was closed without merging.")]
    [InlineData(DispatchState.Failed, "GitHub Copilot could not be started.")]
    [InlineData(DispatchState.Ended, "The session with GitHub Copilot was ended.")]
    public void A_session_is_described_in_a_sentence(DispatchState state, string expected)
    {
        var session = new AgentSessionDto
        {
            Agent = "copilot",
            State = state,
            PullRequestNumber = 77,
            Repository = "contoso/checkout",
            BaseBranch = "main",
            DispatchedBy = Guid.NewGuid(),
            DispatchedAtUtc = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc)
        };

        Assert.Equal(expected, AgentLabels.Describe(session));
        Assert.Equal("https://github.com/contoso/checkout/pull/77", session.PullRequestUrl);
    }

    [Theory]
    [InlineData(PullRequestState.Open, true, "Draft")]
    [InlineData(PullRequestState.Open, false, "Open")]
    [InlineData(PullRequestState.Merged, false, "Merged")]
    [InlineData(PullRequestState.Closed, true, "Closed")]
    public void A_pull_request_is_labeled_by_its_state_not_by_color_alone(PullRequestState state, bool isDraft, string expected)
    {
        Assert.Equal(expected, AgentLabels.StateOf(new PullRequestDto(77, state, isDraft, "contoso/checkout")));
    }

    [Fact]
    public void The_agent_chip_shows_only_cards_with_an_open_session()
    {
        var filter = new BoardFilter();
        DateOnly today = new(2026, 11, 16);
        CardDto withAgent = Card("Checkout", Implement, Location.Active) with { Agent = new CardAgentDto("copilot", DispatchState.Working, null) };
        CardDto without = Card("Receipts", Implement, Location.Active);

        filter.ToggleAgent();

        Assert.True(filter.IsActive);
        Assert.True(filter.Matches(withAgent, today));
        Assert.False(filter.Matches(without, today));

        filter.Clear();
        Assert.False(filter.AgentOnly);
        Assert.True(filter.Matches(without, today));
    }

    [Fact]
    public async Task The_callback_hands_the_api_what_github_sent_back_escaped()
    {
        var server = new StubServer();

        await server.CreateApi().CompleteGitHubConnectionAsync("a code&more", "s t=1", Ct);

        string path = server.Last(HttpMethod.Get).Path;
        var query = System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..]);
        Assert.StartsWith("github/callback?", path, StringComparison.Ordinal);
        Assert.Equal(("a code&more", "s t=1"), (query["code"], query["state"]));
    }

    [Fact]
    public async Task The_profile_reads_the_connection_and_starting_one_says_where_to_go()
    {
        var server = new StubServer();
        server.OnGet("github/connection", new GitHubConnectionDto("octocat"));
        server.OnGet("github/connect", new ConnectGitHubResponse("https://github.com/login/oauth/authorize?client_id=x&state=y"));
        KanbanApi api = server.CreateApi();

        object? connection = (await api.GetGitHubConnectionAsync(Ct)).Value;
        object? start = (await api.StartGitHubConnectionAsync(Ct)).Value;

        Assert.True(Assert.IsType<GitHubConnectionDto>(connection).IsConnected);
        Assert.EndsWith("&state=y", Assert.IsType<ConnectGitHubResponse>(start).AuthorizeUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sending_a_card_posts_the_agent_branch_and_instructions_and_reads_the_session_back()
    {
        var server = new StubServer();
        Guid card = Guid.NewGuid();
        server.On(HttpMethod.Post, $"cards/{card}/agent-dispatch", System.Net.HttpStatusCode.Created, """
            { "agent": "copilot", "state": "Working", "repository": "contoso/checkout", "baseBranch": "main",
              "dispatchedBy": "0a000000-0000-4000-8000-000000000001", "dispatchedAtUtc": "2026-11-16T09:00:00Z" }
            """);

        object? result = (await server.CreateApi().DispatchAgentAsync(card, new DispatchAgentRequest("copilot", "main", "Be careful."), Ct)).Value;

        AgentSessionDto session = Assert.IsType<AgentSessionDto>(result);
        Assert.Equal((DispatchState.Working, "main", null), (session.State, session.BaseBranch, session.PullRequestNumber));
        Assert.Equal("""{"agent":"copilot","baseBranch":"main","instructions":"Be careful."}""", server.Last(HttpMethod.Post).Body);
    }
}
