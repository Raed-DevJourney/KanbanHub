namespace KanbanHub.Domain.GitHub;

/// <summary>Something happened to an issue. <paramref name="Label"/> names the label of a labeled or unlabeled action.</summary>
public sealed record class IssueChanged(IssueSnapshot Issue, string Action, string? Label = null)
{
    public const string Opened = "opened";
    public const string Edited = "edited";
    public const string Labeled = "labeled";
    public const string Unlabeled = "unlabeled";
    public const string Closed = "closed";
    public const string Reopened = "reopened";

    /// <summary>Not a webhook action: a backfill or the reconciler listed the issue.</summary>
    public const string Seen = "seen";
}

/// <summary>Someone commented on an issue. Only people with write access to the repository may give commands.</summary>
public sealed record class IssueCommented(IssueRef Issue, long CommentId, string Body, string AuthorLogin, bool AuthorCanWrite);

/// <summary>A pull request was opened, changed, merged or closed: its text says which issues it closes.</summary>
public sealed record class PullRequestChanged(RepoRef Repository, int Number, string HeadSha, string? Body)
{
    public PullRequestState State { get; init; } = PullRequestState.Open;

    public bool IsDraft { get; init; }

    /// <summary>Who opened it: an agent's pull request is recognized by its author.</summary>
    public string AuthorLogin { get; init; } = "";
}

/// <summary>Someone was unassigned from an issue. Unassigning an agent is how people stop it on GitHub.</summary>
public sealed record class AgentUnassigned(IssueRef Issue, string AssigneeLogin);

public sealed record class RepositoryRenamed(long RepositoryId, string FullName);

/// <summary>An event the sync doesn't act on.</summary>
public sealed record class Unhandled;

/// <summary>Everything a webhook delivery can mean to the sync. A new kind fails the build until it is routed.</summary>
public union GitHubEvent(IssueChanged, IssueCommented, PullRequestChanged, AgentUnassigned, RepositoryRenamed, Unhandled);

/// <summary>A webhook delivery as it arrived, after its signature was checked.</summary>
public sealed record class RawDelivery(string DeliveryId, string EventName, string Payload);

/// <summary>A delivery the parser understood: who caused it and what it means.</summary>
public sealed record class ParsedDelivery(string DeliveryId, string EventName, string SenderLogin, GitHubEvent Event);
