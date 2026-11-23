namespace KanbanHub.Domain.GitHub;

/// <summary>
/// The "Kanban done rule" check run on pull requests (section 8.3): a pull request that says it closes a linked
/// issue gets a check listing the card's done-rule items. It passes when the required ones are ticked, so a
/// repository can require it before merging. <see cref="PullRequestTracker"/> decides which pull requests those are.
/// </summary>
public sealed class DoneRuleCheck(
    IGitHubClient github, IIssueLinkStore links, IPullRequestStore pullRequests, ISyncedCards cards, SyncJournal journal)
{
    /// <summary>
    /// Brings the check runs of the card's open pull requests up to date, for example after an item was ticked.
    /// A merged or closed pull request keeps the check it ended with.
    /// </summary>
    public async Task RefreshAsync(Guid cardId, CancellationToken cancellationToken)
    {
        PullRequestLink[] open =
        [
            .. (await pullRequests.ListAsync(cardId, cancellationToken)).Where(pullRequest => pullRequest.State is PullRequestState.Open)
        ];
        if (open.Length == 0) return;
        if (await links.FindByCardAsync(cardId, cancellationToken) is not { } link) return;
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return;

        IReadOnlyList<ChecklistLine> checklist = await cards.ListChecklistAsync(cardId, cancellationToken);
        foreach (PullRequestLink pullRequest in open)
            await SaveAsync(link.Issue.Repo, pullRequest, DoneRuleSummary.For(pullRequest.HeadSha, card.Number, checklist), cancellationToken);
    }

    private async Task SaveAsync(RepoRef repository, PullRequestLink pullRequest, CheckRun run, CancellationToken cancellationToken)
    {
        string field = SyncFields.CheckRunOf(pullRequest.Number);
        string fingerprint = $"{run.HeadSha}\n{run.Summary}";
        if (!await journal.IsNewsAsync(pullRequest.CardId, field, fingerprint, cancellationToken)) return;

        long checkRunId = await github.SaveCheckRunAsync(repository, pullRequest.CheckRunId, run, cancellationToken);
        await pullRequests.SaveAsync(pullRequest with { CheckRunId = checkRunId }, cancellationToken);
        await journal.RecordAsync(pullRequest.CardId, SyncDirection.Outbound, field, fingerprint, cancellationToken);
    }
}
