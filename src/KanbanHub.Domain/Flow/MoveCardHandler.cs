using System.Security.Claims;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Flow;

/// <summary>
/// Moves a card for a user. Checks permission and legality here, then hands the move to the
/// repository, whose procedure enforces the done rule, WIP limits and concurrency (section 5).
/// </summary>
public sealed class MoveCardHandler(ICardRepository cards, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public async Task<MoveOutcome> HandleAsync(
        MoveCardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        CardOnBoard card = await cards.GetOnBoardAsync(command.CardId, cancellationToken);
        Permission granted = await authorizer.GetEffectiveAsync(user, card.BoardId, cancellationToken);

        if (!granted.HasFlag(Permission.CardMove))
            return new Forbidden(Permission.CardMove);
        if (await cards.FindAppliedMoveAsync(command.CardId, command.IdempotencyKey, cancellationToken) is { } applied)
            return applied;   // a retry (rule R10): the card has moved since, so judging the move again would be wrong
        if (card.PlanMoveTo(command.ToStepId, command.ToLocation) is not { } plan)
            return IllegalTransition.UnknownColumn;
        if (plan.FindMissingPermission(granted) is { } missing)
            return new Forbidden(missing);
        if (plan.FindIllegality(hasReason: command.Reason is not null) is { } illegality)
            return new IllegalTransition(illegality);

        return await cards.MoveAsync(CreateRequest(command, plan, user, granted), cancellationToken);
    }

    private MoveRequest CreateRequest(
        MoveCardCommand command, MovePlan plan, ClaimsPrincipal user, Permission granted) => new()
        {
            CardId = command.CardId,
            ExpectedRowVersion = command.ExpectedRowVersion,
            To = plan.To,
            ToRank = command.ToRank,
            ActorId = user.GetObjectId(),
            Source = command.Source,
            IdempotencyKey = command.IdempotencyKey,
            Now = clock.GetUtcNow(),
            OverrideReason = granted.HasFlag(Permission.WipOverride) ? command.Reason : null,
            Reason = plan.IsBackward ? command.Reason : null
        };
}
