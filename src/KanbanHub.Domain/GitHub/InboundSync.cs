using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// Applies what happened to an issue to its card (section 9.2). Every change goes through the use cases people
/// use; what a use case refuses is noted on the link, shown on the card and explained in the status comment.
/// </summary>
public sealed class InboundSync(
    IIssueLinkStore links, IssueText text, LabelRequests labels, IssueClosure closure, IssueImport import, OutboundSync outbound)
{
    /// <summary>Returns false when the issue concerns no card.</summary>
    public async Task<bool> ApplyAsync(IssueChanged change, CancellationToken cancellationToken)
    {
        IssueRef issue = change.Issue.Issue;
        if (change.Issue.IsPullRequest) return false;
        if (await links.FindByIssueAsync(issue.RepositoryId, issue.Number, cancellationToken) is not { } link)
            return MayBeNew(change) && await import.TryImportAsync(change.Issue, cancellationToken);

        string? problem = await ApplyToCardAsync(link, change, cancellationToken);
        await NoteAsync(link.CardId, change, problem, cancellationToken);

        // Settle: put back any maintained label GitHub's side changed, and bring the status comment up to date.
        await outbound.PushAsync(link.CardId, cancellationToken);
        return true;
    }

    /// <summary>A mirrored repository's issue becomes a card when it is opened, listed, or given the mirrored label.</summary>
    private static bool MayBeNew(IssueChanged change) =>
        change.Action is IssueChanged.Opened or IssueChanged.Seen or IssueChanged.Labeled or IssueChanged.Reopened;

    private Task<string?> ApplyToCardAsync(IssueLink link, IssueChanged change, CancellationToken cancellationToken) =>
        change switch
        {
            { Action: IssueChanged.Edited or IssueChanged.Seen } => text.ApplyAsync(link.CardId, change.Issue, cancellationToken),
            { Action: IssueChanged.Labeled or IssueChanged.Unlabeled } => labels.ApplyAsync(link.CardId, change, cancellationToken),
            { Action: IssueChanged.Closed } => closure.ClosedAsync(link, change.Issue, cancellationToken),
            { Action: IssueChanged.Reopened } => closure.ReopenedAsync(link, cancellationToken),
            _ => Task.FromResult<string?>(null)
        };

    /// <summary>
    /// Remembers why a request was refused, or that the latest one went through. A text edit that succeeds
    /// leaves an earlier note alone: it says nothing about the move that note is about.
    /// </summary>
    private async Task NoteAsync(Guid cardId, IssueChanged change, string? problem, CancellationToken cancellationToken)
    {
        bool isTextOnly = change.Action is IssueChanged.Edited or IssueChanged.Seen;
        if (problem is null && isTextOnly) return;
        if (await links.FindByCardAsync(cardId, cancellationToken) is not { } current || current.SyncProblem == problem) return;

        await links.SaveStateAsync(current with { SyncProblem = Fit(problem) }, cancellationToken);
    }

    private static string? Fit(string? problem) => problem is { Length: > MaxProblemLength } ? problem[..MaxProblemLength] : problem;

    private const int MaxProblemLength = 400;
}

/// <summary>Title and text: last writer wins, and a value that was the last one synced is never applied again.</summary>
public sealed class IssueText(GitHubEdits edits, SyncJournal journal)
{
    public async Task<string?> ApplyAsync(Guid cardId, IssueSnapshot issue, CancellationToken cancellationToken)
    {
        bool title = await journal.ShouldApplyAsync(cardId, SyncFields.Title, issue.Title, issue.UpdatedAt, cancellationToken);
        bool body = await journal.ShouldApplyAsync(cardId, SyncFields.Body, issue.Body, issue.UpdatedAt, cancellationToken);
        if (!title && !body) return null;

        string? problem = await edits.EditAsync(cardId, content => Merge(content, issue, (title, body)), cancellationToken);
        if (problem is not null) return problem;

        if (title) await journal.RecordAsync(cardId, SyncDirection.Inbound, SyncFields.Title, issue.Title, cancellationToken);
        if (body) await journal.RecordAsync(cardId, SyncDirection.Inbound, SyncFields.Body, issue.Body, cancellationToken);
        return null;
    }

    private static CardContent Merge(CardContent content, IssueSnapshot issue, (bool Title, bool Body) take) => content with
    {
        Title = take.Title ? issue.Title : content.Title,
        Description = take.Body ? (string.IsNullOrWhiteSpace(issue.Body) ? null : issue.Body) : content.Description
    };
}

/// <summary>
/// What a maintained label means when someone adds or removes it in GitHub (section 9.4): a priority, a type,
/// blocked, or a request to move the card. Taking a place label off asks for nothing.
/// </summary>
public sealed class LabelRequests(ISyncedCards cards, GitHubEdits edits, GitHubMoves moves, SyncJournal journal)
{
    public async Task<string?> ApplyAsync(Guid cardId, IssueChanged change, CancellationToken cancellationToken)
    {
        if (change.Label is not { } label || !KanbanLabels.IsReserved(label)) return null;
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return null;

        // The issue's maintained labels are no longer what was last synced. Recording that makes the sync
        // write them again afterwards, which takes off any label that turned out not to be true.
        await journal.RecordAsync(cardId, SyncDirection.Inbound, SyncFields.Labels, MaintainedOn(change.Issue), cancellationToken);
        bool isAdded = change.Action is IssueChanged.Labeled;

        if (KanbanLabels.PriorityOf(label) is { } bucket) return await SetPriorityAsync(card, bucket, isAdded, cancellationToken);
        if (KanbanLabels.TypeOf(label) is { } type)
            return isAdded ? await edits.EditAsync(cardId, content => content with { CardType = type }, cancellationToken) : null;
        if (KanbanLabels.Same(label, KanbanLabels.Blocked)) return await SetBlockedAsync(card, isAdded, cancellationToken);

        return isAdded ? await MoveAsync(card, TargetOf(label, card), label, cancellationToken) : null;
    }

    private static string MaintainedOn(IssueSnapshot issue) =>
        string.Join(',', issue.Labels.Where(KanbanLabels.IsReserved).Order(StringComparer.Ordinal));

    private async Task<string?> SetPriorityAsync(SyncedCard card, byte bucket, bool isAdded, CancellationToken cancellationToken)
    {
        byte? wanted = isAdded ? bucket : null;
        bool changes = isAdded ? card.Content.PriorityBucket != bucket : card.Content.PriorityBucket == bucket;
        return changes
            ? await edits.EditAsync(card.CardId, content => content with { PriorityBucket = wanted }, cancellationToken)
            : null;
    }

    private async Task<string?> SetBlockedAsync(SyncedCard card, bool isAdded, CancellationToken cancellationToken)
    {
        if (card.IsBlocked == isAdded) return null;

        return isAdded
            ? await edits.BlockAsync(card.CardId, GitHubEdits.BlockedInGitHub, cancellationToken)
            : await edits.UnblockAsync(card.CardId, cancellationToken);
    }

    private static MoveTarget? TargetOf(string label, SyncedCard card) =>
        KanbanLabels.Same(label, KanbanLabels.Done)
            ? MoveTargets.Done(card)
            : MoveTargets.Active(KanbanLabels.StepOf(label, card.Steps));

    private async Task<string?> MoveAsync(SyncedCard card, MoveTarget? target, string label, CancellationToken cancellationToken)
    {
        if (target is null) return $"The label {label} names no column this card can go to.";
        if (card.StepId == target.StepId && card.Location == target.Location) return null;   // already there

        MoveOutcome outcome = await moves.RequestAsync(card, target, cancellationToken);
        return outcome.Value is Moved ? null : MoveReplies.Describe(outcome, target);
    }
}

/// <summary>
/// Closing and reopening in GitHub (section 9.4). A close is a request, not a move: the card is marked
/// "closed in GitHub" and the team finishes it here, where the done rule is checked. "Not planned" cancels the
/// card when the app may cancel on this board.
/// </summary>
public sealed class IssueClosure(IIssueLinkStore links, ISyncedCards cards, GitHubMoves moves, SyncJournal journal)
{
    public const string NotPlannedReason = "Closed as not planned in GitHub";

    public async Task<string?> ClosedAsync(IssueLink link, IssueSnapshot issue, CancellationToken cancellationToken)
    {
        await journal.RecordAsync(link.CardId, SyncDirection.Inbound, SyncFields.State, issue.State.ToString(), cancellationToken);
        if (await cards.FindAsync(link.CardId, cancellationToken) is not { } card || IsOutOfFlow(card)) return null;
        if (issue.State is IssueState.NotPlanned && (await moves.CancelAsync(card, NotPlannedReason, cancellationToken)).Value is Moved)
            return null;

        await links.SaveStateAsync(link with { ClosedInGitHub = true }, cancellationToken);
        return issue.State is IssueState.NotPlanned ? $"{NotPlannedReason}. Cancel the card here if that is right." : null;
    }

    public async Task<string?> ReopenedAsync(IssueLink link, CancellationToken cancellationToken)
    {
        await journal.RecordAsync(link.CardId, SyncDirection.Inbound, SyncFields.State, nameof(IssueState.Open), cancellationToken);
        await links.SaveStateAsync(link with { ClosedInGitHub = false }, cancellationToken);

        return await cards.FindAsync(link.CardId, cancellationToken) is { } card && IsOutOfFlow(card)
            ? "Reopened in GitHub. Bring the card back into flow here if the work continues."
            : null;
    }

    private static bool IsOutOfFlow(SyncedCard card) => card.IsFinished || card.Location is Location.Canceled;
}

/// <summary>Turns an issue of a mirrored repository into a backlog card (section 9.5).</summary>
public sealed class IssueImport(
    IRepoLinkStore repositories, IIssueLinkStore links, Backlog backlog, OutboundSync outbound, SyncSettings settings, TimeProvider clock)
{
    public async Task<bool> TryImportAsync(IssueSnapshot issue, CancellationToken cancellationToken)
    {
        if (await repositories.FindAsync(issue.Issue.RepositoryId, cancellationToken) is not { } repository) return false;
        if (!repository.Mirrors(issue)) return false;

        Outcome<CardCreated> added = await backlog.AddAsync(repository.BoardId, ContentOf(issue), settings.Principal, cancellationToken);
        if (added.Value is not CardCreated card) return false;

        var link = new IssueLink(card.CardId, issue.Issue);
        if (!await links.LinkAsync(link, issue.NodeId, clock.GetUtcNow(), cancellationToken)) return false;

        await outbound.AdoptAsync(link, cancellationToken);
        return true;
    }

    private static CardContent ContentOf(IssueSnapshot issue) => new()
    {
        Title = issue.Title.Length > CardContent.MaxTitleLength ? issue.Title[..CardContent.MaxTitleLength] : issue.Title,
        Description = string.IsNullOrWhiteSpace(issue.Body) ? null : issue.Body,
        CardType = issue.Labels.Select(KanbanLabels.TypeOf).FirstOrDefault(type => type is not null) ?? CardType.Feature,
        PriorityBucket = issue.Labels.Select(KanbanLabels.PriorityOf).FirstOrDefault(bucket => bucket is not null)
    };
}
