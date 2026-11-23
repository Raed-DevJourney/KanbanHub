using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Contracts;

/// <summary>A card as the board shows it. The description and checklist come with <see cref="CardDetailDto"/>.</summary>
public sealed record CardDto
{
    public required Guid Id { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public CardType CardType { get; init; }

    public byte? PriorityBucket { get; init; }

    public string? Origin { get; init; }

    public Location Location { get; init; }

    public Guid? StepId { get; init; }

    public decimal Rank { get; init; }

    /// <summary>On the ordered Ready list at the top of the backlog.</summary>
    public bool IsReady { get; init; }

    public Guid? ParentId { get; init; }

    /// <summary>For a parked card, the step it may return to.</summary>
    public Guid? ParkedFromStepId { get; init; }

    /// <summary>A parent that has been broken down into tasks.</summary>
    public bool IsContainer { get; init; }

    public bool IsBlocked { get; init; }

    public string? BlockedReason { get; init; }

    public DateOnly? DueDate { get; init; }

    public DateTime EnteredLocationAtUtc { get; init; }

    /// <summary>Send this back in <c>If-Match</c> when moving or editing the card.</summary>
    public required string ETag { get; init; }

    /// <summary>The GitHub issue the card is linked to, when it is.</summary>
    public GitHubIssueDto? Issue { get; init; }

    /// <summary>The agent session open on the card, when there is one (rule R12: the card still occupies WIP).</summary>
    public CardAgentDto? Agent { get; init; }
}

/// <summary>A card's GitHub issue, and what the sync has to say about it.</summary>
public sealed record GitHubIssueDto(string Repository, int Number)
{
    /// <summary>Why the last change asked for in GitHub was not applied (the "sync issue" badge of section 6.3).</summary>
    public string? SyncProblem { get; init; }

    /// <summary>Closed in GitHub while the card is still in flow: the team finishes or cancels it here.</summary>
    public bool ClosedInGitHub { get; init; }

    public string Url => $"https://github.com/{Repository}/issues/{Number}";
}

/// <summary>A repository that feeds a board.</summary>
public sealed record RepoLinkDto(long RepositoryId, string FullName, KanbanHub.Domain.GitHub.SyncMode Mode, string? LabelFilter);

/// <summary>The body of <c>POST /boards/{id}/github/links</c>: the repository as <c>owner/name</c> and how to sync it.</summary>
public sealed record LinkRepositoryRequest(string Repository, KanbanHub.Domain.GitHub.SyncMode Mode, string? LabelFilter = null);

/// <summary>The body of <c>POST /cards/{id}/github/link</c>. Without a number, a new issue is created from the card.</summary>
public sealed record LinkIssueRequest(long RepositoryId, int? IssueNumber = null);

public sealed record ChecklistItemDto(Guid Id, Guid StepId, string Text, bool IsRequired, bool IsChecked);

/// <summary><c>GET /cards/{id}</c>: the card plus what its side panel shows.</summary>
public sealed record CardDetailDto
{
    public required CardDto Card { get; init; }

    public required Guid BoardId { get; init; }

    public required Guid CreatedBy { get; init; }

    public string? Description { get; init; }

    public string? TargetRelease { get; init; }

    public short? EstimatedTasks { get; init; }

    public short? ActualTasks { get; init; }

    public BlockerCategory? BlockerCategory { get; init; }

    public string? CanceledReason { get; init; }

    public IReadOnlyList<ChecklistItemDto> Checklist { get; init; } = [];

    /// <summary>The pull requests that say they close the card's issue.</summary>
    public IReadOnlyList<PullRequestDto> PullRequests { get; init; } = [];

    /// <summary>Cards the duplicate finder thinks are the same work, until someone says they are not.</summary>
    public IReadOnlyList<DuplicateDto> Duplicates { get; init; } = [];

    /// <summary>The fields a <c>PATCH</c> may change.</summary>
    public CardContent ToContent() => new()
    {
        Title = Card.Title,
        Description = Description,
        CardType = Card.CardType,
        PriorityBucket = Card.PriorityBucket,
        Origin = Card.Origin,
        DueDate = Card.DueDate,
        TargetRelease = TargetRelease,
        EstimatedTasks = EstimatedTasks
    };
}

public sealed record CardCreatedDto(Guid Id, int Number);

/// <summary>The body of <c>POST /cards/{id}/move</c>. <c>If-Match</c> and <c>Idempotency-Key</c> travel as headers.</summary>
public sealed record MoveCardBody(Guid? ToStepId, Location ToLocation, decimal? ToRank = null, string? OverrideReason = null);

public sealed record CancelCardRequest(string? Reason);

/// <summary>The answer to blocking or unblocking a card: where that left it, so the UI can say so.</summary>
public sealed record BlockResponse(BlockPlacement Placement);

public sealed record BreakDownRequest(IReadOnlyList<string> Titles);

public sealed record BreakDownResponse(IReadOnlyList<Guid> CardIds);

public sealed record AddCommentRequest(string? Body);

/// <summary>A comment on a card. <paramref name="ExternalAuthor"/> is the GitHub login when it was written there.</summary>
public sealed record CommentDto(Guid Id, Guid AuthorId, string Body, DateTime CreatedAtUtc, string? ExternalAuthor = null);

public sealed record CommentAddedDto(Guid Id);

/// <summary>One line of a person's inbox.</summary>
public sealed record NotificationDto
{
    public required long Id { get; init; }

    public required Guid BoardId { get; init; }

    public Guid? CardId { get; init; }

    public required KanbanHub.Domain.Notifications.NotificationKind Kind { get; init; }

    public required string Title { get; init; }

    public string? Detail { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    public bool IsRead { get; init; }
}

/// <summary><c>GET /me/notifications</c>: what the inbox shows, and the number for the badge.</summary>
public sealed record InboxDto(IReadOnlyList<NotificationDto> Notifications, int UnreadCount);

/// <summary>A person's channels for one kind of notification; some kinds can't be turned off in the app.</summary>
public sealed record NotificationPreferenceDto(
    KanbanHub.Domain.Notifications.NotificationKind Kind, bool InApp, bool Email, bool CanTurnOffInApp);

public sealed record SetNotificationPreferenceRequest(KanbanHub.Domain.Notifications.NotificationKind Kind, bool InApp, bool Email);

public sealed record SnoozeRequest(int Hours);

/// <summary>Subscribes the caller's real-time connection to a board's changes.</summary>
public sealed record JoinBoardRequest(string ConnectionId);
