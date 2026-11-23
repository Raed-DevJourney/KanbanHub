using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>Says why a use case refused a change that GitHub asked for, or null when it didn't.</summary>
internal static class Refusals
{
    public static string? Explain<T>(Outcome<T> outcome)
        where T : notnull => outcome.Value switch
        {
            Forbidden forbidden => $"The GitHub App lacks the {forbidden.Missing} permission on this board.",
            NotFound missing => $"There is no such {missing.What}.",
            Invalid invalid => string.Join(" ", invalid.Problems),
            ConcurrencyConflict => "The card changed at the same moment.",
            TenantRoleRequired required => $"The GitHub App lacks the tenant role {required.Role}.",
            _ => null
        };
}

/// <summary>
/// Edits cards on GitHub's behalf: as the app's own principal, through the same use cases people use.
/// Each method returns null when the change was made, or the reason it wasn't.
/// </summary>
public sealed class GitHubEdits(ISyncedCards cards, CardCommands commands, CardBlocking blocking, SyncSettings settings)
{
    public const string BlockedInGitHub = "Blocked in GitHub";

    public async Task<string?> EditAsync(Guid cardId, Func<CardContent, CardContent> change, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return "The card no longer exists.";

        var edit = new CardEdit(cardId, card.RowVersion, change);
        return Refusals.Explain(await commands.EditAsync(edit, settings.Principal, cancellationToken));
    }

    public async Task<string?> BlockAsync(Guid cardId, string reason, CancellationToken cancellationToken) =>
        Refusals.Explain(await blocking.BlockAsync(
            cardId, new Blocker(reason, BlockerCategory.External), settings.Principal, cancellationToken));

    public async Task<string?> UnblockAsync(Guid cardId, CancellationToken cancellationToken) =>
        Refusals.Explain(await blocking.UnblockAsync(cardId, settings.Principal, cancellationToken));
}

/// <summary>
/// Asks for moves on GitHub's behalf. They pass the same gates as anyone's: permission, flow rules,
/// done rule and WIP limits (rule R11), and never carry an override.
/// </summary>
public sealed class GitHubMoves(MoveCardHandler mover, CancelCardHandler canceler, SyncSettings settings)
{
    public async Task<MoveOutcome> RequestAsync(SyncedCard card, MoveTarget target, CancellationToken cancellationToken) =>
        await mover.HandleAsync(
            new MoveCardCommand
            {
                CardId = card.CardId,
                ExpectedRowVersion = card.RowVersion,
                ToStepId = target.StepId,
                ToLocation = target.Location,
                IdempotencyKey = Guid.NewGuid(),
                Source = MoveSource.GitHub
            },
            settings.Principal, cancellationToken);

    public async Task<MoveOutcome> CancelAsync(SyncedCard card, string reason, CancellationToken cancellationToken) =>
        await canceler.HandleAsync(
            new CancelCardCommand
            {
                CardId = card.CardId,
                ExpectedRowVersion = card.RowVersion,
                Reason = reason,
                IdempotencyKey = Guid.NewGuid(),
                Source = MoveSource.GitHub
            },
            settings.Principal, cancellationToken);
}
