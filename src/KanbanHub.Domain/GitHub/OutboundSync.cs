using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// Makes a card's issue say what the card says (section 9.3): title, text, the maintained labels, open or closed,
/// the status comment and new comments. It sends only what differs from the last sync, so running it again,
/// or for an event GitHub itself caused, sends nothing.
/// </summary>
public sealed class OutboundSync(
    IGitHubClient github, IIssueLinkStore links, ISyncedCards cards, SyncJournal journal, SyncSettings settings)
{
    public Task PushAsync(Guid cardId, CancellationToken cancellationToken) =>
        PushAsync(cardId, everything: false, cancellationToken);

    /// <summary>The same without trusting the ledger, for "sync now": everything is written again.</summary>
    public Task PushEverythingAsync(Guid cardId, CancellationToken cancellationToken) =>
        PushAsync(cardId, everything: true, cancellationToken);

    /// <summary>
    /// Starts a link. Each side keeps the title and text it has: they are taken as already in sync,
    /// and from here on a change on either side reaches the other.
    /// </summary>
    public async Task AdoptAsync(IssueLink link, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(link.CardId, cancellationToken) is not { } card) return;

        await journal.RecordAsync(card.CardId, SyncDirection.Outbound, SyncFields.Title, card.Content.Title, cancellationToken);
        await journal.RecordAsync(card.CardId, SyncDirection.Outbound, SyncFields.Body, card.Content.Description, cancellationToken);
        await PushAsync(card.CardId, cancellationToken);
    }

    /// <summary>One run: the card as it is now, its link, and whether the ledger is to be trusted.</summary>
    private sealed record Push(SyncedCard Card, IssueLink Link, bool Everything)
    {
        public Guid CardId => Card.CardId;

        public string MaintainedLabels => string.Join(',', KanbanLabels.For(Card));

        public IssueState WantedState =>
            Card.Location is Location.Canceled ? IssueState.NotPlanned
            : Card.IsFinished ? IssueState.Completed
            : IssueState.Open;
    }

    private async Task PushAsync(Guid cardId, bool everything, CancellationToken cancellationToken)
    {
        if (await links.FindByCardAsync(cardId, cancellationToken) is not { } link) return;
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return;

        var push = new Push(card, await SettledAsync(link, card, cancellationToken), everything);
        await PushIssueAsync(push, cancellationToken);
        await PushStatusCommentAsync(push, cancellationToken);
        await PushCommentsAsync(push, cancellationToken);
    }

    /// <summary>Once the card is finished or canceled, "closed in GitHub while still in flow" no longer applies.</summary>
    private async Task<IssueLink> SettledAsync(IssueLink link, SyncedCard card, CancellationToken cancellationToken)
    {
        if (!link.ClosedInGitHub || !(card.IsFinished || card.Location is Location.Canceled)) return link;

        IssueLink settled = link with { ClosedInGitHub = false };
        await links.SaveStateAsync(settled, cancellationToken);
        return settled;
    }

    private async Task PushIssueAsync(Push push, CancellationToken cancellationToken)
    {
        IssueChange change = await PlanAsync(push, cancellationToken);
        if (change.IsEmpty) return;

        await github.UpdateIssueAsync(push.Link.Issue, change, cancellationToken);
        await RecordAsync(push, change, cancellationToken);
    }

    private async Task<IssueChange> PlanAsync(Push push, CancellationToken cancellationToken)
    {
        string title = push.Card.Content.Title;
        string body = push.Card.Content.Description ?? "";

        return new IssueChange
        {
            Title = await IsNewsAsync(push, SyncFields.Title, title, cancellationToken) ? title : null,
            Body = await IsNewsAsync(push, SyncFields.Body, body, cancellationToken) ? body : null,
            Labels = await PlanLabelsAsync(push, cancellationToken),
            State = await PlanStateAsync(push, cancellationToken)
        };
    }

    /// <summary>The issue's own labels plus the maintained ones, or null when the maintained ones are as last synced.</summary>
    private async Task<IReadOnlyList<string>?> PlanLabelsAsync(Push push, CancellationToken cancellationToken)
    {
        if (!await IsNewsAsync(push, SyncFields.Labels, push.MaintainedLabels, cancellationToken)) return null;

        IssueSnapshot? issue = await github.FindIssueAsync(push.Link.Issue, cancellationToken);
        return issue is null ? null : KanbanLabels.Merge(issue.Labels, KanbanLabels.For(push.Card));
    }

    /// <summary>Closes the issue when the card is finished or canceled; reopens it only if this sync closed it.</summary>
    private async Task<IssueState?> PlanStateAsync(Push push, CancellationToken cancellationToken)
    {
        LedgerEntry? last = await journal.LastAsync(push.CardId, SyncFields.State, cancellationToken);
        bool write = push.WantedState is IssueState.Open
            ? FieldSync.ShouldReopen(last)
            : push.Everything || FieldSync.ShouldPush(push.WantedState.ToString(), last);

        return write ? push.WantedState : null;
    }

    private async Task RecordAsync(Push push, IssueChange sent, CancellationToken cancellationToken)
    {
        if (sent.Title is not null) await RecordAsync(push, SyncFields.Title, sent.Title, cancellationToken);
        if (sent.Body is not null) await RecordAsync(push, SyncFields.Body, sent.Body, cancellationToken);
        if (sent.Labels is not null) await RecordAsync(push, SyncFields.Labels, push.MaintainedLabels, cancellationToken);
        if (sent.State is { } state) await RecordAsync(push, SyncFields.State, state.ToString(), cancellationToken);
    }

    private async Task PushStatusCommentAsync(Push push, CancellationToken cancellationToken)
    {
        string text = StatusComment.Render(push.Card, push.Link, settings.BoardUrl(push.Card.BoardId));
        if (!await IsNewsAsync(push, SyncFields.StatusComment, text, cancellationToken)) return;

        // Edited in place; written anew the first time, and again if someone deleted it on GitHub.
        bool edited = push.Link.StatusCommentId is { } commentId
            && await github.EditCommentAsync(push.Link.Issue.Repo, commentId, text, cancellationToken);
        if (!edited)
        {
            long created = await github.AddCommentAsync(push.Link.Issue, text, cancellationToken);
            await links.SaveStateAsync(push.Link with { StatusCommentId = created }, cancellationToken);
        }

        await RecordAsync(push, SyncFields.StatusComment, text, cancellationToken);
    }

    private async Task PushCommentsAsync(Push push, CancellationToken cancellationToken)
    {
        foreach (OutgoingComment comment in await cards.ListUnsentCommentsAsync(push.CardId, cancellationToken))
        {
            string text = $"**{MarkdownText.Inline(comment.AuthorName)}** wrote in KanbanHub:\n\n{comment.Body}";
            long sent = await github.AddCommentAsync(push.Link.Issue, text, cancellationToken);
            await cards.MarkCommentSentAsync(comment.CommentId, sent, cancellationToken);
        }
    }

    private async Task<bool> IsNewsAsync(Push push, string field, string value, CancellationToken cancellationToken) =>
        push.Everything || await journal.IsNewsAsync(push.CardId, field, value, cancellationToken);

    private Task RecordAsync(Push push, string field, string value, CancellationToken cancellationToken) =>
        journal.RecordAsync(push.CardId, SyncDirection.Outbound, field, value, cancellationToken);
}
