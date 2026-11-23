using System.Security.Claims;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Flow;

/// <summary>A caller's request to cancel a card (FR-C10).</summary>
public sealed record class CancelCardCommand
{
    public required Guid CardId { get; init; }

    public required byte[] ExpectedRowVersion { get; init; }

    public string? Reason
    {
        get;
        init => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public required Guid IdempotencyKey { get; init; }

    public MoveSource Source { get; init; } = MoveSource.User;
}

/// <summary>
/// Cancels a card from wherever it is. Canceling is a move like any other, so it goes through
/// the same procedure; it frees the card's WIP slot and keeps the card searchable.
/// </summary>
public sealed class CancelCardHandler(ICardRepository cards, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const string ReasonRequired = "Canceling a card needs a reason.";

    public async Task<MoveOutcome> HandleAsync(
        CancelCardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        CardOnBoard card = await cards.GetOnBoardAsync(command.CardId, cancellationToken);

        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardCancel, cancellationToken) is { } denied)
            return denied;
        if (await cards.FindAppliedMoveAsync(command.CardId, command.IdempotencyKey, cancellationToken) is { } applied)
            return applied;
        if (command.Reason is null)
            return new IllegalTransition(ReasonRequired);

        return await cards.MoveAsync(CreateRequest(command, user), cancellationToken);
    }

    private MoveRequest CreateRequest(CancelCardCommand command, ClaimsPrincipal user) => new()
    {
        CardId = command.CardId,
        ExpectedRowVersion = command.ExpectedRowVersion,
        To = CardPosition.OutsideSteps(Location.Canceled),
        ActorId = user.GetObjectId(),
        Source = command.Source,
        IdempotencyKey = command.IdempotencyKey,
        Now = clock.GetUtcNow(),
        Reason = command.Reason
    };
}
