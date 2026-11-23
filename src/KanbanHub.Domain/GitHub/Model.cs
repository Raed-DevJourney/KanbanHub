using System.Security.Claims;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>A repository as the app reaches it: through the installation that grants access.</summary>
public readonly record struct RepoRef(long InstallationId, long RepositoryId);

/// <summary>One issue of a repository. GitHub numbers pull requests in the same series.</summary>
public readonly record struct IssueRef(long InstallationId, long RepositoryId, int Number)
{
    public RepoRef Repo => new(InstallationId, RepositoryId);
}

public enum SyncMode : byte
{
    /// <summary>People link single issues to cards, or create an issue from a card.</summary>
    OnDemand = 1,

    /// <summary>Every open issue (that carries the link's label, when one is set) becomes a backlog card.</summary>
    Mirror = 2
}

/// <summary>A repository linked to a board.</summary>
public sealed record class RepoLink(Guid BoardId, RepoRef Repo, string FullName, SyncMode Mode, string? LabelFilter)
{
    public bool Mirrors(IssueSnapshot issue) =>
        Mode is SyncMode.Mirror
        && issue.State is IssueState.Open
        && (LabelFilter is null || issue.Labels.Contains(LabelFilter, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A card linked to an issue, with what the sync remembers between its runs.</summary>
public sealed record class IssueLink(Guid CardId, IssueRef Issue)
{
    /// <summary>The one comment the app keeps up to date on the issue.</summary>
    public long? StatusCommentId { get; init; }

    /// <summary>Why the last change asked for in GitHub was not applied; shown on the card and in the status comment.</summary>
    public string? SyncProblem { get; init; }

    /// <summary>The issue was closed in GitHub while the card is still in flow: the team decides what that means.</summary>
    public bool ClosedInGitHub { get; init; }
}

public enum IssueState
{
    Open,
    Completed,
    NotPlanned
}

/// <summary>An issue as GitHub reports it, in a webhook payload or from the REST API.</summary>
public sealed record class IssueSnapshot
{
    public required IssueRef Issue { get; init; }

    public required string NodeId { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public IssueState State { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsPullRequest { get; init; }
}

/// <summary>What to write to an issue. Null members are left as they are in GitHub.</summary>
public sealed record class IssueChange
{
    public string? Title { get; init; }

    public string? Body { get; init; }

    /// <summary>The complete label set the issue should carry.</summary>
    public IReadOnlyList<string>? Labels { get; init; }

    public IssueState? State { get; init; }

    public bool IsEmpty => Title is null && Body is null && Labels is null && State is null;
}

/// <summary>A page of a repository's issues and the number of the last page there is.</summary>
public sealed record class IssuePage(IReadOnlyList<IssueSnapshot> Issues, int LastPage);

/// <summary>Which issues to list: open ones with a label, or everything updated since a moment.</summary>
public sealed record class IssueQuery(int Page, string? Label, DateTimeOffset? UpdatedSince);

public sealed record class RepositoryInfo(RepoRef Repo, string FullName);

/// <summary>The "Kanban done rule" check run on a pull request's latest commit.</summary>
public sealed record class CheckRun(string HeadSha, bool Passed, string Summary)
{
    public const string Name = "Kanban done rule";
}

/// <summary>Where a pull request stands. Stored in <c>kb.GitHubPullRequest.State</c>, so never renumber.</summary>
public enum PullRequestState : byte
{
    Open = 1,
    Merged = 2,

    /// <summary>Closed without merging.</summary>
    Closed = 3
}

/// <summary>A pull request that closes a linked issue.</summary>
public sealed record class PullRequestLink(long RepositoryId, int Number, Guid CardId, string HeadSha, long? CheckRunId)
{
    public PullRequestState State { get; init; } = PullRequestState.Open;

    public bool IsDraft { get; init; }
}

/// <summary>A workflow step as the sync needs it: where a label points, and the WIP the status comment reports.</summary>
public sealed record class SyncedStep(Guid Id, string Name, int Order)
{
    public bool HasBlockedColumn { get; init; }

    public bool IsFinal { get; init; }

    /// <summary>Coding agents may work on cards in this step (section 10.2).</summary>
    public bool IsAgentEligible { get; init; }

    public int WipCount { get; init; }

    public int? WipLimit { get; init; }
}

/// <summary>A card as the sync reads it, with its board's steps.</summary>
public sealed record class SyncedCard
{
    public required Guid CardId { get; init; }

    public required Guid BoardId { get; init; }

    public required int Number { get; init; }

    public required byte[] RowVersion { get; init; }

    public required CardContent Content { get; init; }

    public required Location Location { get; init; }

    public Guid? StepId { get; init; }

    public bool IsBlocked { get; init; }

    public string? BlockedReason { get; init; }

    public required IReadOnlyList<SyncedStep> Steps { get; init; }

    public SyncedStep? Step => Steps.FirstOrDefault(step => step.Id == StepId);

    /// <summary>In the final step's Done: the work is finished.</summary>
    public bool IsFinished => Location is Location.Done && Step is { IsFinal: true };
}

public sealed record class ChecklistLine(string Text, bool IsRequired, bool IsChecked);

/// <summary>A comment written in KanbanHub that the issue doesn't have yet.</summary>
public sealed record class OutgoingComment(Guid CommentId, string AuthorName, string Body);

/// <summary>
/// Who the GitHub App is inside KanbanHub and how to recognize it in GitHub. Changes that arrive from GitHub
/// are made as this principal, which holds its own role on each board it syncs (section 3: service identities
/// never borrow a person's role).
/// </summary>
public sealed record class SyncSettings(Guid PrincipalId, string BotLogin, Uri? WebBaseUrl)
{
    public ClaimsPrincipal Principal { get; } = new(new ClaimsIdentity(
        [new Claim("oid", PrincipalId.ToString()), new Claim("name", "GitHub")], authenticationType: "github-app"));

    /// <summary>True for events our own writes caused: they are dropped, never synced back (section 9.2).</summary>
    public bool IsOwnBot(string login) => string.Equals(login, BotLogin, StringComparison.OrdinalIgnoreCase);

    public string? BoardUrl(Guid boardId) => WebBaseUrl is null ? null : new Uri(WebBaseUrl, $"boards/{boardId}").ToString();
}

/// <summary>GitHub's rate limit said to wait. The writer reschedules the work for <see cref="RetryAt"/> (section 9.6).</summary>
public sealed class GitHubRateLimitedException(DateTimeOffset retryAt) : Exception($"GitHub asked to wait until {retryAt:O}.")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
