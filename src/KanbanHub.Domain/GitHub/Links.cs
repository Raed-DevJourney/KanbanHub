using System.Security.Claims;
using System.Text.RegularExpressions;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.GitHub;

/// <summary>What a board owner submits to link a repository: its <c>owner/name</c> and how to sync it.</summary>
public sealed record class LinkRepositoryCommand(string Repository, SyncMode Mode, string? LabelFilter);

/// <summary>Which issue to link a card to; without a number a new issue is created from the card.</summary>
public sealed record class LinkIssueCommand(long RepositoryId, int? IssueNumber);

/// <summary>Which repositories feed a board (section 9.5). Only people with <c>github.link.configure</c> decide that.</summary>
public sealed partial class RepositoryLinks(
    IRepoLinkStore repositories, IGitHubClient github, IBoardAuthorizer authorizer, SyncSettings settings, TimeProvider clock)
{
    public const int MaxLabelLength = 100;

    public async Task<Outcome<IReadOnlyList<RepoLink>>> ListAsync(Guid boardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.BoardRead, cancellationToken) is { } denied) return denied;

        return new Outcome<IReadOnlyList<RepoLink>>(await repositories.ListAsync(boardId, cancellationToken));
    }

    public async Task<Outcome<RepoLink>> LinkAsync(
        Guid boardId, LinkRepositoryCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.GitHubLinkConfigure, cancellationToken) is { } denied) return denied;
        if (FindProblem(command) is { } problem) return new Invalid(problem);
        if (await github.FindRepositoryAsync(command.Repository.Trim(), cancellationToken) is not { } found)
            return new Invalid($"The KanbanHub GitHub App isn't installed on {command.Repository}, or there is no such repository.");

        var link = new RepoLink(boardId, found.Repo, found.FullName, command.Mode, NullIfBlank(command.LabelFilter));
        bool saved = await repositories.SaveAsync(link, settings.PrincipalId, StampFor(user), cancellationToken);
        return saved ? link : new Invalid($"{found.FullName} already feeds another board. A repository feeds one board.");
    }

    public async Task<Outcome<Saved>> UnlinkAsync(Guid boardId, long repositoryId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.GitHubLinkConfigure, cancellationToken) is { } denied) return denied;

        bool removed = await repositories.RemoveAsync(boardId, repositoryId, StampFor(user), cancellationToken);
        return removed ? Saved.Instance : new NotFound("repository link");
    }

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());

    private static string? FindProblem(LinkRepositoryCommand command) =>
        !FullName().IsMatch(command.Repository.Trim()) ? "Name the repository as owner/name."
        : !Enum.IsDefined(command.Mode) ? "Unknown sync mode."
        : command.LabelFilter?.Length > MaxLabelLength ? $"The label can be at most {MaxLabelLength} characters."
        : null;

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex FullName();
}

/// <summary>Links single cards to issues, creates an issue from a card, and syncs a card on request (section 9.5).</summary>
public sealed class IssueLinks(
    IIssueLinkStore links, IRepoLinkStore repositories, ISyncedCards cards, IGitHubClient github, OutboundSync outbound,
    IBoardAuthorizer authorizer, TimeProvider clock)
{
    private static readonly NotFound NoSuchCard = new("card");
    private static readonly NotFound NoSuchLink = new("GitHub link");

    public async Task<Outcome<IssueLink>> LinkAsync(
        Guid cardId, LinkIssueCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardEdit, cancellationToken) is { } denied) return denied;
        if (await links.FindByCardAsync(cardId, cancellationToken) is not null) return new Invalid("This card is linked to an issue already.");
        if (await repositories.FindAsync(command.RepositoryId, cancellationToken) is not { } repository || repository.BoardId != card.BoardId)
            return new Invalid("That repository isn't linked to this card's board.");
        if (await FindOrCreateIssueAsync(repository, card, command.IssueNumber, cancellationToken) is not { IsPullRequest: false } issue)
            return new Invalid($"{repository.FullName} has no issue with that number.");

        var link = new IssueLink(cardId, issue.Issue);
        if (!await links.LinkAsync(link, issue.NodeId, clock.GetUtcNow(), cancellationToken))
            return new Invalid("That issue is linked to another card already.");

        await outbound.AdoptAsync(link, cancellationToken);
        return link;
    }

    public async Task<Outcome<Saved>> UnlinkAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardEdit, cancellationToken) is { } denied) return denied;

        return await links.UnlinkAsync(cardId, cancellationToken) ? Saved.Instance : NoSuchLink;
    }

    /// <summary>"Sync now": writes the card's state to its issue again, whatever the ledger says was sent before.</summary>
    public async Task<Outcome<Saved>> ResyncAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.GitHubSyncTrigger, cancellationToken) is { } denied) return denied;
        if (await links.FindByCardAsync(cardId, cancellationToken) is null) return NoSuchLink;

        await outbound.PushEverythingAsync(cardId, cancellationToken);
        return Saved.Instance;
    }

    private Task<IssueSnapshot?> FindOrCreateIssueAsync(
        RepoLink repository, SyncedCard card, int? issueNumber, CancellationToken cancellationToken) =>
        issueNumber is { } number
            ? github.FindIssueAsync(new IssueRef(repository.Repo.InstallationId, repository.Repo.RepositoryId, number), cancellationToken)
            : CreateIssueAsync(repository, card, cancellationToken);

    private async Task<IssueSnapshot?> CreateIssueAsync(RepoLink repository, SyncedCard card, CancellationToken cancellationToken) =>
        await github.CreateIssueAsync(
            repository.Repo, new IssueChange { Title = card.Content.Title, Body = card.Content.Description ?? "" }, cancellationToken);
}
