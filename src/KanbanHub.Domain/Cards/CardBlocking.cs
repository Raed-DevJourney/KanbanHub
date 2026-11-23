using System.Security.Claims;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Cards;

/// <summary>Where blocking or unblocking left the card.</summary>
public enum BlockPlacement
{
    /// <summary>It stays where it was: it wasn't in the sub-column the change starts from, or its step has no Blocked sub-column.</summary>
    Unmoved,

    /// <summary>Blocking moved it from Active to its step's Blocked sub-column, out of the WIP count.</summary>
    MovedToBlocked,

    /// <summary>Unblocking brought it back to Active.</summary>
    ReturnedToActive,

    /// <summary>It is unblocked, but its step is full: it waits in the Blocked sub-column until a slot opens.</summary>
    WaitingForSlot
}

/// <summary>What blocking or unblocking did: the card's new row version and where it is now.</summary>
public sealed record class BlockChange(byte[] RowVersion, BlockPlacement Placement);

/// <summary>
/// Blocks and unblocks cards. A card blocked while in Active moves to its step's Track sub-column, which the
/// board shows as "Blocked": it stands out and stops counting toward WIP. Unblocking brings it back when the
/// step has room (rule R5); otherwise it waits there.
/// </summary>
public sealed class CardBlocking(ICardStore cards, ICardRepository board, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const int MaxReasonLength = 500;

    private static readonly NotFound NoSuchCard = new("card");

    public async Task<Outcome<BlockChange>> BlockAsync(
        Guid cardId, Blocker blocker, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardBlock, cancellationToken) is { } denied)
            return denied;
        if (string.IsNullOrWhiteSpace(blocker.Reason) || blocker.Reason.Length > MaxReasonLength || !Enum.IsDefined(blocker.Category))
            return new Invalid($"Blocking a card needs a reason of up to {MaxReasonLength} characters and a category.");

        var stamp = new Stamp(user.GetObjectId(), clock.GetUtcNow());
        CardVersion blocked = await cards.SetBlockedAsync(cardId, blocker, stamp, cancellationToken);
        MoveOutcome? shifted = await ShiftAsync(cardId, blocked, Shift.OutOfActive, stamp, cancellationToken);

        return shifted?.Value is Moved moved
            ? new BlockChange(moved.RowVersion, BlockPlacement.MovedToBlocked)
            : new BlockChange(blocked.RowVersion, BlockPlacement.Unmoved);
    }

    public async Task<Outcome<BlockChange>> UnblockAsync(Guid cardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardBlock, cancellationToken) is { } denied)
            return denied;

        var stamp = new Stamp(user.GetObjectId(), clock.GetUtcNow());
        CardVersion unblocked = await cards.SetBlockedAsync(cardId, blocker: null, stamp, cancellationToken);
        MoveOutcome? shifted = await ShiftAsync(cardId, unblocked, Shift.BackToActive, stamp, cancellationToken);

        return shifted?.Value switch
        {
            Moved moved => new BlockChange(moved.RowVersion, BlockPlacement.ReturnedToActive),
            WipLimitReached or OverrideReasonRequired => new BlockChange(unblocked.RowVersion, BlockPlacement.WaitingForSlot),
            _ => new BlockChange(unblocked.RowVersion, BlockPlacement.Unmoved)
        };
    }

    /// <summary>
    /// Moves the card between its step's Active and Blocked sub-columns through the enforcing procedure, so coming
    /// back is WIP-checked like any other move. Returns null when the card isn't where the shift starts, or its
    /// step has no Blocked sub-column. The permission to block covers this move.
    /// </summary>
    private async Task<MoveOutcome?> ShiftAsync(
        Guid cardId, CardVersion version, Shift shift, Stamp stamp, CancellationToken cancellationToken)
    {
        CardOnBoard card = await board.GetOnBoardAsync(cardId, cancellationToken);
        if (card.Position.Location != shift.From || card.PlanMoveTo(card.Position.StepId, shift.To) is not { } plan)
            return null;

        return await board.MoveAsync(
            new MoveRequest
            {
                CardId = cardId,
                ExpectedRowVersion = version.RowVersion,
                To = plan.To,
                ActorId = stamp.ActorId,
                Source = MoveSource.User,
                IdempotencyKey = Guid.NewGuid(),
                Now = stamp.Now,
                Reason = shift.Why
            },
            cancellationToken);
    }

    /// <summary>A move within a step that blocking or unblocking causes, and the reason recorded with it.</summary>
    private sealed record Shift(Location From, Location To, string Why)
    {
        public static Shift OutOfActive { get; } = new(Location.Active, Location.Track, "Blocked");

        public static Shift BackToActive { get; } = new(Location.Track, Location.Active, "Unblocked");
    }
}
