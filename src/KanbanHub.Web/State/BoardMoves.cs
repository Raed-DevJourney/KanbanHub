using KanbanHub.Contracts;

namespace KanbanHub.Web.State;

/// <summary>A move that waits for the person to say why (a backward move, or one past an "allow with reason" limit).</summary>
public sealed record PendingMove(CardDto Card, BoardSlot Target);

/// <summary>
/// Everything a person can do to move a card, whether they drag it or use the keyboard:
/// both end in the same request, and both may be asked for a reason first.
/// </summary>
public sealed class BoardMoves(BoardStore store, KeyboardMove keyboard, Notices notices)
{
    public event Action? Changed;

    public PendingMove? AwaitingReason { get; private set; }

    /// <summary>The card to give keyboard focus back to once the board has re-rendered.</summary>
    public Guid? CardToFocus { get; private set; }

    public async Task MoveAsync(CardDto card, BoardSlot target, CancellationToken cancellationToken)
    {
        MoveResult result = await store.MoveAsync(card, target, reason: null, cancellationToken);
        AwaitingReason = result is MoveResult.ReasonRequired ? new PendingMove(card, target) : null;
        Changed?.Invoke();
    }

    public async Task ConfirmReasonAsync(string reason, CancellationToken cancellationToken)
    {
        if (AwaitingReason is not { } pending) return;

        AwaitingReason = null;
        await store.MoveAsync(pending.Card, pending.Target, reason, cancellationToken);
        Changed?.Invoke();
    }

    public void CancelReason()
    {
        AwaitingReason = null;
        Changed?.Invoke();
    }

    // ---- keyboard (FR-M07)

    public void PickUp(CardDto card)
    {
        keyboard.PickUp(card, BoardSlot.AllOf(store.Steps));
        Announce($"Picked up {card.Title}. Use the left and right arrow keys to choose a column, Space to drop, Escape to cancel.");
    }

    public void Step(int direction)
    {
        keyboard.Step(direction);
        if (keyboard is { Card: { } card, Target: { } target }) Announce(Describe(card, target));
    }

    public async Task DropCarriedAsync(CancellationToken cancellationToken)
    {
        if (keyboard.Release() is not var (card, target)) return;

        CardToFocus = card.Id;
        if (target.Holds(card)) Announce($"{card.Title} stays in {target.Label}.");
        else await MoveAsync(card, target, cancellationToken);
        Changed?.Invoke();
    }

    public void CancelCarry()
    {
        if (keyboard.Release() is not var (card, _)) return;

        CardToFocus = card.Id;
        Announce($"Move canceled. {card.Title} stays where it was.");
    }

    public void FocusRestored() => CardToFocus = null;

    private string Describe(CardDto card, BoardSlot target)
    {
        if (target.Holds(card)) return $"{target.Label}, where the card is now.";

        MovePreview preview = store.Preview(card, target);
        string verdict = !preview.IsLegal ? "A card can't move there from here."
            : preview.IsFull ? "This step is full."
            : preview.NeedsReason ? "Dropping here needs a reason."
            : "Press Space to drop.";
        return $"{target.Label}. {store.DescribeWip(target)} {verdict}";
    }

    private void Announce(string message)
    {
        notices.Announce(message);
        Changed?.Invoke();
    }
}
