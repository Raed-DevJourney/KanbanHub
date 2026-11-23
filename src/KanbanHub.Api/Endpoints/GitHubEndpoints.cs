using System.Text;
using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain;
using KanbanHub.Domain.GitHub;
using KanbanHub.GitHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>
/// Receives the GitHub App's webhook deliveries (section 9.2). It checks the signature, queues the raw payload
/// and answers at once: GitHub wants a 2xx within ten seconds, and the work happens behind the queue.
/// </summary>
public sealed class GitHubWebhookEndpoint(WebhookVerifier verifier, IGitHubInbox inbox)
{
    /// <summary>Authenticated by its HMAC signature, not by an Entra token: the auth middleware lets this function through.</summary>
    [Function("GitHubWebhook")]
    public async Task<IActionResult> Receive(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "github/webhook")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        byte[] body = await ReadAsync(request, cancellationToken);
        if (!verifier.IsGenuine(body, request.Headers["X-Hub-Signature-256"].ToString()))
            return Problems.InvalidSignature();

        string deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
        string eventName = request.Headers["X-GitHub-Event"].ToString();
        if (deliveryId.Length == 0 || eventName.Length == 0) return Problems.BodyRequired();

        await inbox.EnqueueAsync(new RawDelivery(deliveryId, eventName, Encoding.UTF8.GetString(body)), cancellationToken);
        return new AcceptedResult();
    }

    private static async Task<byte[]> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

/// <summary>Which repositories feed a board, and which issue a card is (section 9.5).</summary>
public sealed class GitHubLinkEndpoints(RepositoryLinks repositories, IssueLinks issues)
{
    [Function("ListRepositoryLinks")]
    public async Task<IActionResult> ListRepositories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/github/links")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<RepoLink>> outcome = await repositories.ListAsync(boardId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(links => new OkObjectResult(links.Select(ToDto).ToList()));
    }

    [Function("LinkRepository")]
    public async Task<IActionResult> LinkRepository(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards/{boardId:guid}/github/links")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<LinkRepositoryRequest>(cancellationToken) is not { Repository: not null } body)
            return Problems.BodyRequired();

        var command = new LinkRepositoryCommand(body.Repository, body.Mode, body.LabelFilter);
        return await GitHubCalls.GuardAsync(async () =>
            (await repositories.LinkAsync(boardId, command, request.HttpContext.User, cancellationToken))
            .ToResult(link => new OkObjectResult(ToDto(link))));
    }

    [Function("UnlinkRepository")]
    public async Task<IActionResult> UnlinkRepository(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v1/boards/{boardId:guid}/github/links/{repositoryId:long}")] HttpRequest request,
        Guid boardId, long repositoryId, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await repositories.UnlinkAsync(boardId, repositoryId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    /// <summary>Links the card to an existing issue, or creates one from the card when the body names no number.</summary>
    [Function("LinkIssue")]
    public async Task<IActionResult> LinkIssue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/github/link")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<LinkIssueRequest>(cancellationToken) is not { } body) return Problems.BodyRequired();

        var command = new LinkIssueCommand(body.RepositoryId, body.IssueNumber);
        return await GitHubCalls.GuardAsync(async () =>
            (await issues.LinkAsync(cardId, command, request.HttpContext.User, cancellationToken))
            .ToResult(_ => new NoContentResult()));
    }

    [Function("UnlinkIssue")]
    public async Task<IActionResult> UnlinkIssue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v1/cards/{cardId:guid}/github/link")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await issues.UnlinkAsync(cardId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("SyncCardWithGitHub")]
    public Task<IActionResult> Sync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/cards/{cardId:guid}/github/sync")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        GitHubCalls.GuardAsync(async () =>
            (await issues.ResyncAsync(cardId, request.HttpContext.User, cancellationToken)).ToResult(OutcomeResults.NoContent));

    private static RepoLinkDto ToDto(RepoLink link) => new(link.Repo.RepositoryId, link.FullName, link.Mode, link.LabelFilter);
}

/// <summary>A request that has to reach GitHub while the caller waits says so plainly when GitHub doesn't answer.</summary>
internal static class GitHubCalls
{
    public static async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is HttpRequestException or GitHubRateLimitedException)
        {
            return Problems.GitHubUnavailable();
        }
    }
}
