using System.Text.Json;
using KanbanHub.Domain.Agents;
using KanbanHub.Domain.Events;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// The event router (section 9.2): takes a webhook delivery, drops what was handled before and what our own
/// writes caused, and hands the rest to the part of the sync it concerns.
/// </summary>
public sealed class InboundProcessor(
    IWebhookDeliveries deliveries, InboundSync issues, IssueComments comments, PullRequestTracker pullRequests,
    AgentTracking agents, IRepoLinkStore repositories, SyncSettings settings, TimeProvider clock)
{
    public async Task HandleAsync(ParsedDelivery delivery, CancellationToken cancellationToken)
    {
        if (await deliveries.WasHandledAsync(delivery.DeliveryId, cancellationToken)) return;

        bool handled = !settings.IsOwnBot(delivery.SenderLogin) && await RouteAsync(delivery.Event, cancellationToken);
        DeliveryStatus status = handled ? DeliveryStatus.Handled : DeliveryStatus.Ignored;
        await deliveries.RecordAsync(delivery, status, clock.GetUtcNow(), cancellationToken);
    }

    private Task<bool> RouteAsync(GitHubEvent gitHubEvent, CancellationToken cancellationToken) => gitHubEvent switch
    {
        IssueChanged issue => issues.ApplyAsync(issue, cancellationToken),
        IssueCommented comment => comments.HandleAsync(comment, cancellationToken),
        PullRequestChanged pullRequest => pullRequests.TrackAsync(pullRequest, cancellationToken),
        AgentUnassigned unassigned => agents.UnassignedAsync(unassigned, cancellationToken),
        RepositoryRenamed renamed => RenameAsync(renamed, cancellationToken),
        Unhandled => Task.FromResult(false),
    };

    private async Task<bool> RenameAsync(RepositoryRenamed renamed, CancellationToken cancellationToken)
    {
        await repositories.RenameAsync(renamed.RepositoryId, renamed.FullName, cancellationToken);
        return true;
    }
}

/// <summary>
/// Stands in for the inbound queue where there is no Service Bus (a development machine, tests):
/// the delivery is processed at once, in this process.
/// </summary>
public sealed class InlineGitHubInbox(IGitHubEventParser parser, InboundProcessor processor) : IGitHubInbox
{
    public Task EnqueueAsync(RawDelivery delivery, CancellationToken cancellationToken) =>
        processor.HandleAsync(parser.Parse(delivery), cancellationToken);
}

/// <summary>Imports and refreshes a repository's issues page by page (section 9.5): the backfill and the nightly reconciler.</summary>
public sealed class Backfill(IGitHubClient github, InboundSync inbound)
{
    /// <summary>
    /// Handles one page: linked issues are refreshed, issues a mirror wants become cards.
    /// Returns the number of the last page, so the caller can fan out over the rest.
    /// </summary>
    public async Task<int> SyncPageAsync(RepoLink repository, int page, DateTimeOffset? updatedSince, CancellationToken cancellationToken)
    {
        var query = new IssueQuery(page, repository.LabelFilter, updatedSince);
        IssuePage found = await github.ListIssuesAsync(repository.Repo, query, cancellationToken);

        foreach (IssueSnapshot issue in found.Issues)
            await inbound.ApplyAsync(new IssueChanged(issue, IssueChanged.Seen), cancellationToken);

        return found.LastPage;
    }

    public async Task SyncAllAsync(RepoLink repository, DateTimeOffset? updatedSince, CancellationToken cancellationToken)
    {
        int lastPage = await SyncPageAsync(repository, page: 1, updatedSince, cancellationToken);
        for (int page = 2; page <= lastPage; page++)
            await SyncPageAsync(repository, page, updatedSince, cancellationToken);
    }
}

/// <summary>The board events the GitHub sync answers.</summary>
public static class GitHubBoardEvents
{
    /// <summary>A repository was linked to a board; the payload carries its <c>repositoryId</c>.</summary>
    public const string RepositoryLinked = "github.repo.linked";

    public static bool IsAboutACard(BoardEvent boardEvent) => boardEvent.Type != RepositoryLinked;

    public static long RepositoryIdOf(BoardEvent boardEvent)
    {
        using JsonDocument payload = JsonDocument.Parse(boardEvent.Payload);
        return payload.RootElement.GetProperty("repositoryId").GetInt64();
    }
}

/// <summary>The GitHub writer (section 9.3): every change to a linked card is pushed to its issue.</summary>
public sealed class GitHubWriter(OutboundSync outbound, DoneRuleCheck checks, IIssueLinkStore links) : IBoardEventConsumer
{
    public async Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        if (!GitHubBoardEvents.IsAboutACard(boardEvent)) return;
        if (await links.FindByCardAsync(boardEvent.CardId, cancellationToken) is not { } link) return;

        if (boardEvent.Type == BoardEvent.CardMoved && link.SyncProblem is not null)
            await links.SaveStateAsync(link with { SyncProblem = null }, cancellationToken);   // the card moved on: the old refusal is history

        await outbound.PushAsync(boardEvent.CardId, cancellationToken);
        await checks.RefreshAsync(boardEvent.CardId, cancellationToken);
    }
}

/// <summary>Answers "a repository was linked" by importing its issues, page by page (section 9.5).</summary>
public sealed class BackfillOnLink(IRepoLinkStore repositories, Backfill backfill) : IBoardEventConsumer
{
    public async Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        if (GitHubBoardEvents.IsAboutACard(boardEvent)) return;
        if (await repositories.FindAsync(GitHubBoardEvents.RepositoryIdOf(boardEvent), cancellationToken) is not { } repository) return;

        await backfill.SyncAllAsync(repository, updatedSince: null, cancellationToken);
    }
}
