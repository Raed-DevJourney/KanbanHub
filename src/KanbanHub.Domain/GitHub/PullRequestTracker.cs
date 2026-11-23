using KanbanHub.Domain.Agents;

namespace KanbanHub.Domain.GitHub;

/// <summary>
/// Follows pull requests that say they close a linked issue: remembers them and their state on the card,
/// keeps their done-rule check current, and lets an agent session follow the pull request the agent opened.
/// </summary>
public sealed class PullRequestTracker(
    IIssueLinkStore links, IPullRequestStore pullRequests, DoneRuleCheck checks, AgentTracking agents)
{
    /// <summary>Returns false when the pull request closes no linked issue.</summary>
    public async Task<bool> TrackAsync(PullRequestChanged change, CancellationToken cancellationToken)
    {
        bool tracked = false;
        foreach (int issueNumber in ClosingKeywords.FindIssueNumbers(change.Body))
        {
            if (await links.FindByIssueAsync(change.Repository.RepositoryId, issueNumber, cancellationToken) is not { } link) continue;

            PullRequestLink pullRequest = await RememberAsync(link.CardId, change, cancellationToken);
            await checks.RefreshAsync(link.CardId, cancellationToken);
            await agents.FollowAsync(pullRequest, change.AuthorLogin, cancellationToken);
            tracked = true;
        }

        return tracked;
    }

    /// <summary>A new commit needs a new check run; the same commit keeps the one it has.</summary>
    private async Task<PullRequestLink> RememberAsync(Guid cardId, PullRequestChanged change, CancellationToken cancellationToken)
    {
        PullRequestLink? known = (await pullRequests.ListAsync(cardId, cancellationToken))
            .FirstOrDefault(pullRequest => pullRequest.Number == change.Number);
        long? checkRunId = known?.HeadSha == change.HeadSha ? known.CheckRunId : null;

        var pullRequest = new PullRequestLink(change.Repository.RepositoryId, change.Number, cardId, change.HeadSha, checkRunId)
        {
            State = change.State,
            IsDraft = change.IsDraft
        };
        await pullRequests.SaveAsync(pullRequest, cancellationToken);
        return pullRequest;
    }
}
