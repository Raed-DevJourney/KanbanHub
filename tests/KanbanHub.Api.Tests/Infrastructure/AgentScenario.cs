using System.Security.Claims;
using KanbanHub.Agents;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Infrastructure;

/// <summary>
/// A board with a linked repository whose owner has connected GitHub, and what tests do with agents: connect
/// people, send cards, and play the agent's side (the pull request it opens arrives as a webhook).
/// </summary>
internal sealed class AgentScenario
{
    private AgentScenario(GitHubScenario gitHub) => GitHub = gitHub;

    public GitHubScenario GitHub { get; }

    public BoardScenario Board => GitHub.Board;

    /// <summary>What "GitHub" was asked to start, and the place to make it refuse.</summary>
    public InMemoryAgentDispatcher Dispatcher => Board.Service<InMemoryAgentDispatcher>();

    public InMemoryGitHubAuthorization Authorization => Board.Service<InMemoryGitHubAuthorization>();

    public static async Task<AgentScenario> StartAsync(KanbanDatabase database, bool agentsEnabled = true)
    {
        var scenario = new AgentScenario(await GitHubScenario.StartAsync(database, agentsEnabled: agentsEnabled));
        Assert.Equal(200, (await scenario.ConnectAsync(scenario.Board.Owner)).StatusOf());
        return scenario;
    }

    /// <summary>"Connect GitHub" as the browser does it: ask where to go, come back with what GitHub appended.</summary>
    public async Task<IActionResult> ConnectAsync(ClaimsPrincipal user)
    {
        IActionResult started = await Board.Connection.Connect(Requests.From(user), Ct);
        var authorizeUrl = new Uri(started.ValueOf<ConnectGitHubResponse>().AuthorizeUrl);

        return await Board.Connection.Complete(Requests.WithQuery(user, authorizeUrl.Query), Ct);
    }

    /// <summary>A member who also holds the Agent Operator role (it is additive, section 7.2), connected to GitHub unless told otherwise.</summary>
    public async Task<ClaimsPrincipal> OperatorAsync(bool connected = true)
    {
        ClaimsPrincipal user = await Board.UserWithAsync(BuiltInRoles.Member);
        await Board.AssignAsync(user.GetObjectId(), PrincipalType.User, BuiltInRoles.AgentOperator);
        if (connected) Assert.Equal(200, (await ConnectAsync(user)).StatusOf());
        return user;
    }

    /// <summary>A card linked to a new issue, in Implement Active: ready for an agent.</summary>
    public Task<(Guid Card, StoredIssue Issue)> ReadyCardAsync(string title = "Checkout") =>
        GitHub.LinkedCardAsync(Board.Implement, Location.Active, title);

    public Task<IActionResult> DispatchAsync(ClaimsPrincipal user, Guid card, string branch = "main", string? instructions = null) =>
        Board.Agents.Dispatch(Requests.From(user, new DispatchAgentRequest("copilot", branch, instructions)), card, Ct);

    public async Task<AgentDeskDto> DeskAsync(ClaimsPrincipal user, Guid card) =>
        (await Board.Agents.Describe(Requests.From(user), card, Ct)).ValueOf<AgentDeskDto>();

    /// <summary>GitHub tells the app about a pull request that says it fixes the issue; Copilot's unless told otherwise.</summary>
    public async Task PullRequestAsync(StoredIssue issue, string action, PullRequestFacts? facts = null)
    {
        PullRequestFacts pullRequest = facts ?? AgentPullRequest(issue);
        Assert.Equal(202, (await GitHub.DeliverAsync("pull_request", GitHub.PullRequestEvent(action, pullRequest))).StatusOf());
    }

    public static PullRequestFacts AgentPullRequest(StoredIssue issue, int number = 77) =>
        new(number, HeadSha: "abc123", Body: $"Fixes #{issue.Reference.Number}") { Author = "Copilot", IsDraft = true };
}
