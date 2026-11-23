using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanHub.Contracts;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.GitHub;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Infrastructure;

/// <summary>A pull request as a webhook describes it.</summary>
internal sealed record PullRequestFacts(int Number, string HeadSha, string Body)
{
    public string Author { get; init; } = "octocat";

    public bool IsDraft { get; init; }

    public bool IsClosed { get; init; }

    public bool IsMerged { get; init; }
}

/// <summary>
/// A board with a repository linked to it, and the two things tests do with GitHub: act as a person there
/// (change the stand-in's issue, then deliver the webhook GitHub would send) and look at what the sync wrote.
/// </summary>
internal sealed class GitHubScenario
{
    private GitHubScenario(BoardScenario board) => Board = board;

    public BoardScenario Board { get; }

    public InMemoryGitHub GitHub => Board.GitHub;

    public RepoLinkDto Repository { get; private set; } = null!;

    public RepoRef Repo => new(InstallationId: 1, Repository.RepositoryId);

    public static async Task<GitHubScenario> StartAsync(
        KanbanDatabase database, SyncMode mode = SyncMode.OnDemand, string? label = null, bool agentsEnabled = true)
    {
        var scenario = new GitHubScenario(await BoardScenario.StartAsync(database, agentsEnabled));
        IActionResult linked = await scenario.LinkRepositoryAsync(scenario.Board.Owner, "contoso/checkout", mode, label);
        scenario.Repository = linked.ValueOf<RepoLinkDto>();
        return scenario;
    }

    public Task<IActionResult> LinkRepositoryAsync(ClaimsPrincipal user, string name, SyncMode mode, string? label = null) =>
        Board.GitHubLinks.LinkRepository(Requests.From(user, new LinkRepositoryRequest(name, mode, label)), Board.BoardId, Ct);

    /// <summary>A card, optionally walked into a column, with an issue created from it.</summary>
    public async Task<(Guid Card, StoredIssue Issue)> LinkedCardAsync(StepDto? step = null, Location location = Location.Active, string title = "Checkout")
    {
        Guid card = await Board.AddCardAsync(title: title);
        if (step is not null) await Board.WalkToAsync(card, step, location);

        IActionResult linked = await LinkIssueAsync(Board.Owner, card, issueNumber: null);
        Assert.Equal(204, linked.StatusOf());
        return (card, await IssueOfAsync(card));
    }

    public Task<IActionResult> LinkIssueAsync(ClaimsPrincipal user, Guid card, int? issueNumber) =>
        Board.GitHubLinks.LinkIssue(Requests.From(user, new LinkIssueRequest(Repository.RepositoryId, issueNumber)), card, Ct);

    public async Task<StoredIssue> IssueOfAsync(Guid card)
    {
        GitHubIssueDto linked = Assert.IsType<GitHubIssueDto>((await Board.CardAsync(card)).Card.Issue);
        return GitHub.IssueOf(new IssueRef(Repo.InstallationId, Repo.RepositoryId, linked.Number));
    }

    /// <summary>The status comment the app keeps on the issue: the only one that starts with its marker.</summary>
    public static string StatusCommentOf(StoredIssue issue) =>
        Assert.Single(issue.Comments.Values, comment => comment.StartsWith(StatusComment.Marker, StringComparison.Ordinal));

    /// <summary>Delivers a webhook as GitHub would: signed with the app's secret, with GitHub's headers.</summary>
    public Task<IActionResult> DeliverAsync(string eventName, object payload, string? deliveryId = null)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        string secret = Board.Service<GitHubAppOptions>().WebhookSecret;
        string signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        return Board.Webhook.Receive(WebhookRequest(body, signature, eventName, deliveryId ?? Guid.NewGuid().ToString()), Ct);
    }

    public static HttpRequest WebhookRequest(byte[] body, string signature, string eventName, string deliveryId)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(body);
        http.Request.Headers["X-Hub-Signature-256"] = signature;
        http.Request.Headers["X-GitHub-Event"] = eventName;
        http.Request.Headers["X-GitHub-Delivery"] = deliveryId;
        return http.Request;
    }

    /// <summary>An <c>issues</c> event for the issue as the stand-in holds it now.</summary>
    public object IssueEvent(string action, StoredIssue issue, string? label = null, string sender = "octocat") => new
    {
        action,
        issue = ToJson(issue),
        label = label is null ? null : new { name = label },
        repository = RepositoryJson,
        installation = new { id = Repo.InstallationId },
        sender = new { login = sender }
    };

    public object CommentEvent(StoredIssue issue, long commentId, string body, string association = "COLLABORATOR") => new
    {
        action = "created",
        issue = ToJson(issue),
        comment = new { id = commentId, body, author_association = association, user = new { login = "octocat" } },
        repository = RepositoryJson,
        installation = new { id = Repo.InstallationId },
        sender = new { login = "octocat" }
    };

    public object PullRequestEvent(string action, int number, string headSha, string body) =>
        PullRequestEvent(action, new PullRequestFacts(number, headSha, body));

    /// <summary>A <c>pull_request</c> event with everything the sync reads from one.</summary>
    public object PullRequestEvent(string action, PullRequestFacts pullRequest) => new
    {
        action,
        pull_request = new
        {
            number = pullRequest.Number,
            body = pullRequest.Body,
            state = pullRequest.IsClosed ? "closed" : "open",
            merged = pullRequest.IsMerged,
            draft = pullRequest.IsDraft,
            user = new { login = pullRequest.Author },
            head = new { sha = pullRequest.HeadSha }
        },
        repository = RepositoryJson,
        installation = new { id = Repo.InstallationId },
        sender = new { login = "octocat" }
    };

    private object RepositoryJson => new { id = Repository.RepositoryId, full_name = Repository.FullName };

    private static object ToJson(StoredIssue issue) => new
    {
        number = issue.Reference.Number,
        node_id = $"I_{issue.Reference.Number}",
        title = issue.Title,
        body = issue.Body,
        labels = issue.Labels.Select(name => new { name }),
        state = issue.State is IssueState.Open ? "open" : "closed",
        state_reason = issue.State switch { IssueState.NotPlanned => "not_planned", IssueState.Completed => "completed", _ => null },
        updated_at = issue.UpdatedAt
    };
}
