using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.Agents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>
/// "Connect GitHub" (section 9.1): links the signed-in person to their GitHub account. The browser can't carry the
/// Entra token through GitHub's redirect, so the web app asks for the address to go to, and its callback page
/// hands back what GitHub returned.
/// </summary>
public sealed class GitHubConnectionEndpoints(GitHubConnection connection)
{
    [Function("GetGitHubConnection")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/github/connection")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        GitHubAccount? account = await connection.FindAsync(request.HttpContext.User, cancellationToken);
        return new OkObjectResult(new GitHubConnectionDto(account?.Login));
    }

    [Function("ConnectGitHub")]
    public async Task<IActionResult> Connect(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/github/connect")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        Uri authorizeUrl = await connection.StartAsync(request.HttpContext.User, cancellationToken);
        return new OkObjectResult(new ConnectGitHubResponse(authorizeUrl.ToString()));
    }

    [Function("CompleteGitHubConnection")]
    public Task<IActionResult> Complete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/github/callback")] HttpRequest request,
        CancellationToken cancellationToken) =>
        GitHubCalls.GuardAsync(async () =>
        {
            string? code = request.Query["code"];
            string? state = request.Query["state"];
            Outcome<GitHubAccount> outcome = await connection.CompleteAsync(code, state, request.HttpContext.User, cancellationToken);
            return outcome.ToResult(account => new OkObjectResult(new GitHubConnectionDto(account.Login)));
        });

    [Function("DisconnectGitHub")]
    public async Task<IActionResult> Disconnect(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v1/github/connection")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await connection.DisconnectAsync(request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }
}

/// <summary>Sending a card to a coding agent, and looking at how that went (section 10).</summary>
public sealed class AgentDispatchEndpoints(AgentDispatches dispatches)
{
    private const string Route = "v1/cards/{cardId:guid}/agent-dispatch";

    /// <summary>What the "Send to agent" form shows: the agents on offer, the board's slots, the card's last session.</summary>
    [Function("DescribeAgentDispatch")]
    public Task<IActionResult> Describe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Route)] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        GitHubCalls.GuardAsync(async () =>
            (await dispatches.DescribeAsync(cardId, request.HttpContext.User, cancellationToken))
            .ToResult(desk => new OkObjectResult(ToDto(desk))));

    [Function("DispatchAgent")]
    public async Task<IActionResult> Dispatch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Route)] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<DispatchAgentRequest>(cancellationToken) is not { Agent: not null, BaseBranch: not null } body)
            return Problems.BodyRequired();

        var command = new DispatchCommand(cardId, body.Agent, body.BaseBranch.Trim(), body.Instructions)
        {
            Model = body.Model,
            CustomAgent = body.CustomAgent
        };
        return await GitHubCalls.GuardAsync(async () =>
            (await dispatches.DispatchAsync(command, request.HttpContext.User, cancellationToken))
            .ToResult(session => OutcomeResults.Created(ToDto(session))));
    }

    /// <summary>Stops tracking the card's open session and frees its slot.</summary>
    [Function("EndAgentSession")]
    public async Task<IActionResult> End(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = Route)] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await dispatches.EndAsync(cardId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    private static AgentDeskDto ToDto(AgentDesk desk) => new()
    {
        IsEnabled = desk.IsEnabled,
        OpenSessions = desk.Capacity.Open,
        SessionLimit = desk.Capacity.Limit,
        GitHubLogin = desk.Account?.Login,
        Agents = [.. desk.Agents.Select(agent => new AgentDto(agent.Key, agent.Name))],
        Repository = desk.Repository,
        Latest = desk.Latest is { } latest ? ToDto(latest) : null,
        WhyNot = desk.WhyNot
    };

    private static AgentSessionDto ToDto(AgentSession session) => new()
    {
        Agent = session.Agent,
        State = session.State,
        PullRequestNumber = session.PullRequestNumber,
        Repository = session.TargetRepository,
        BaseBranch = session.BaseBranch,
        Model = session.Model,
        DispatchedBy = session.DispatchedBy,
        DispatchedAtUtc = session.DispatchedAt.UtcDateTime
    };
}
