namespace KanbanHub.Domain.Flow;

/// <summary>Storage port for moving cards.</summary>
public interface ICardRepository
{
    /// <exception cref="CardNotFoundException">No such card.</exception>
    Task<CardOnBoard> GetOnBoardAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>The result of a move this idempotency key already applied to the card, or null when there is none (rule R10).</summary>
    Task<Moved?> FindAppliedMoveAsync(Guid cardId, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Applies the move if the done rule, WIP limits and row version allow it, in one transaction.</summary>
    /// <exception cref="CardNotFoundException">No such card.</exception>
    Task<MoveOutcome> MoveAsync(MoveRequest request, CancellationToken cancellationToken);
}
