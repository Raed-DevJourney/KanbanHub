using KanbanHub.Domain.Boards;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// What the sync reads from and writes to GitHub: the REST API in Azure, an in-memory stand-in on a development machine.
/// Any call throws <see cref="GitHubRateLimitedException"/> when GitHub says to wait.
/// </summary>
public interface IGitHubClient
{
    /// <summary>The repository and the installation that reaches it, or null when the app isn't installed there.</summary>
    Task<RepositoryInfo?> FindRepositoryAsync(string fullName, CancellationToken cancellationToken);

    Task<IssueSnapshot?> FindIssueAsync(IssueRef issue, CancellationToken cancellationToken);

    Task<IssueSnapshot> CreateIssueAsync(RepoRef repository, IssueChange content, CancellationToken cancellationToken);

    Task UpdateIssueAsync(IssueRef issue, IssueChange change, CancellationToken cancellationToken);

    /// <summary>Returns the new comment's id.</summary>
    Task<long> AddCommentAsync(IssueRef issue, string body, CancellationToken cancellationToken);

    /// <summary>Returns false when the comment is no longer there.</summary>
    Task<bool> EditCommentAsync(RepoRef repository, long commentId, string body, CancellationToken cancellationToken);

    Task<IssuePage> ListIssuesAsync(RepoRef repository, IssueQuery query, CancellationToken cancellationToken);

    /// <summary>Creates the check run, or updates the one <paramref name="checkRunId"/> names. Returns its id.</summary>
    Task<long> SaveCheckRunAsync(RepoRef repository, long? checkRunId, CheckRun run, CancellationToken cancellationToken);
}

/// <summary>Storage port for which repository feeds which board.</summary>
public interface IRepoLinkStore
{
    Task<IReadOnlyList<RepoLink>> ListAsync(Guid boardId, CancellationToken cancellationToken);

    /// <summary>Every linked repository, for the nightly reconciler.</summary>
    Task<IReadOnlyList<RepoLink>> ListAllAsync(CancellationToken cancellationToken);

    Task<RepoLink?> FindAsync(long repositoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the link and gives <paramref name="syncPrincipalId"/> the Member role on the board unless it holds one.
    /// Returns false when the repository already feeds another board.
    /// </summary>
    Task<bool> SaveAsync(RepoLink link, Guid syncPrincipalId, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Removes the link and the issue links of its cards. Returns false when there was no such link.</summary>
    Task<bool> RemoveAsync(Guid boardId, long repositoryId, Stamp stamp, CancellationToken cancellationToken);

    Task RenameAsync(long repositoryId, string fullName, CancellationToken cancellationToken);
}

/// <summary>Storage port for which card is which issue.</summary>
public interface IIssueLinkStore
{
    Task<IssueLink?> FindByCardAsync(Guid cardId, CancellationToken cancellationToken);

    Task<IssueLink?> FindByIssueAsync(long repositoryId, int issueNumber, CancellationToken cancellationToken);

    /// <summary>Returns false when the card or the issue is linked already.</summary>
    Task<bool> LinkAsync(IssueLink link, string issueNodeId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> UnlinkAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>Stores the status comment id, the sync problem and the closed-in-GitHub flag.</summary>
    Task SaveStateAsync(IssueLink link, CancellationToken cancellationToken);
}

public enum SyncDirection : byte
{
    Outbound = 1,
    Inbound = 2
}

/// <summary>One line of the sync ledger: which value of a field went which way, and when (section 9.3).</summary>
public sealed record class LedgerEntry(SyncDirection Direction, string Field, byte[] ValueHash, DateTimeOffset SourceUpdated);

public interface ISyncLedger
{
    Task<LedgerEntry?> FindLastAsync(Guid cardId, string field, CancellationToken cancellationToken);

    Task RecordAsync(Guid cardId, LedgerEntry entry, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>What the sync reads about cards, and the one thing only it writes to them.</summary>
public interface ISyncedCards
{
    Task<SyncedCard?> FindAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>The done-rule items of the step the card is in.</summary>
    Task<IReadOnlyList<ChecklistLine>> ListChecklistAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>Comments written in KanbanHub since the card was linked that the issue doesn't have yet.</summary>
    Task<IReadOnlyList<OutgoingComment>> ListUnsentCommentsAsync(Guid cardId, CancellationToken cancellationToken);

    Task MarkCommentSentAsync(Guid commentId, long gitHubCommentId, CancellationToken cancellationToken);
}

public interface IPullRequestStore
{
    Task SaveAsync(PullRequestLink pullRequest, CancellationToken cancellationToken);

    Task<IReadOnlyList<PullRequestLink>> ListAsync(Guid cardId, CancellationToken cancellationToken);
}

public enum DeliveryStatus : byte
{
    Handled = 1,
    Ignored = 2
}

/// <summary>Remembers which webhook deliveries were dealt with (section 9.2, the second layer of duplicate protection).</summary>
public interface IWebhookDeliveries
{
    Task<bool> WasHandledAsync(string deliveryId, CancellationToken cancellationToken);

    Task RecordAsync(ParsedDelivery delivery, DeliveryStatus status, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Turns GitHub's webhook JSON into the events the sync understands.</summary>
public interface IGitHubEventParser
{
    ParsedDelivery Parse(RawDelivery delivery);

    /// <summary>The key that groups everything about one issue, so its deliveries are handled in order.</summary>
    string SessionKeyOf(RawDelivery delivery);
}

/// <summary>Where a verified webhook delivery waits to be processed: a session queue in Azure, nowhere locally.</summary>
public interface IGitHubInbox
{
    Task EnqueueAsync(RawDelivery delivery, CancellationToken cancellationToken);
}
