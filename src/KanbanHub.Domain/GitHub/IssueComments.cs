using KanbanHub.Domain.Cards;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// A comment on a linked issue is either a command for the card or part of its conversation (section 8.3).
/// Commands are answered on the issue, refusals included; everything else is added to the card's comments.
/// </summary>
public sealed class IssueComments(
    IIssueLinkStore links, Comments comments, CommandRunner commands, IGitHubClient github, OutboundSync outbound, SyncSettings settings)
{
    public const string WriteAccessNeeded = "Only people with write access to this repository can use `/kanban` commands.";

    /// <summary>Returns false when the issue concerns no card.</summary>
    public async Task<bool> HandleAsync(IssueCommented comment, CancellationToken cancellationToken)
    {
        IssueRef issue = comment.Issue;
        if (await links.FindByIssueAsync(issue.RepositoryId, issue.Number, cancellationToken) is not { } link) return false;

        if (SlashCommands.TryParse(comment.Body) is { } command)
            await AnswerAsync(link, comment, command, cancellationToken);
        else
            await comments.AddAsync(DraftOf(link.CardId, comment), settings.Principal, cancellationToken);

        return true;
    }

    private async Task AnswerAsync(IssueLink link, IssueCommented comment, SlashCommand command, CancellationToken cancellationToken)
    {
        string reply = comment.AuthorCanWrite
            ? await commands.RunAsync(link.CardId, command, cancellationToken)
            : WriteAccessNeeded;

        await github.AddCommentAsync(link.Issue, reply, cancellationToken);
        await outbound.PushAsync(link.CardId, cancellationToken);
    }

    /// <summary>GitHub allows longer comments than a card does; the card keeps the beginning.</summary>
    private static CommentDraft DraftOf(Guid cardId, IssueCommented comment)
    {
        string body = comment.Body.Length > Comments.MaxLength ? string.Concat(comment.Body.AsSpan(0, Comments.MaxLength - 1), "…") : comment.Body;
        return new CommentDraft(cardId, body) { Source = new ExternalComment(comment.CommentId, comment.AuthorLogin) };
    }
}

/// <summary>Runs a <c>/kanban</c> command against a card and says how it went.</summary>
public sealed class CommandRunner(ISyncedCards cards, GitHubEdits edits, GitHubMoves moves)
{
    public async Task<string> RunAsync(Guid cardId, SlashCommand command, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return "The card no longer exists.";

        return command switch
        {
            MoveNext => await MoveAsync(card, MoveTargets.Next(card), cancellationToken),
            TrackCard => await MoveAsync(card, MoveTargets.Blocked(card), cancellationToken),
            BlockCard block => await edits.BlockAsync(cardId, block.Reason, cancellationToken) ?? "Blocked.",
            UnblockCard => await edits.UnblockAsync(cardId, cancellationToken) ?? "Unblocked.",
            ShowHelp => SlashCommands.Help,
        };
    }

    private async Task<string> MoveAsync(SyncedCard card, MoveTarget? target, CancellationToken cancellationToken) =>
        target is null
            ? "There is no column to move this card to."
            : MoveReplies.Describe(await moves.RequestAsync(card, target, cancellationToken), target);
}
