using KanbanHub.Contracts;
using KanbanHub.Web.Api;

namespace KanbanHub.Web.State;

/// <summary>The card being dragged with the mouse, so every drop zone can show whether it would take it.</summary>
public sealed class DragState
{
    public event Action? Changed;

    public CardDto? Card { get; private set; }

    public bool IsDragging => Card is not null;

    public void Begin(CardDto card)
    {
        Card = card;
        Changed?.Invoke();
    }

    public void End()
    {
        Card = null;
        Changed?.Invoke();
    }
}

/// <summary>
/// Moving a card with the keyboard (FR-M07): Space picks it up, the arrow keys walk the board's slots,
/// Space drops it and Escape puts it back.
/// </summary>
public sealed class KeyboardMove
{
    private IReadOnlyList<BoardSlot> slots = [];
    private int index;

    public CardDto? Card { get; private set; }

    public bool IsCarrying => Card is not null;

    /// <summary>Where the card would land if dropped now.</summary>
    public BoardSlot? Target => IsCarrying ? slots[index] : null;

    public void PickUp(CardDto card, IReadOnlyList<BoardSlot> boardSlots)
    {
        slots = boardSlots;
        index = Math.Max(0, boardSlots.ToList().FindIndex(slot => slot.Holds(card)));
        Card = card;
    }

    /// <summary>Steps left (-1) or right (+1) and stops at the board's edges.</summary>
    public void Step(int direction)
    {
        if (IsCarrying) index = Math.Clamp(index + direction, 0, slots.Count - 1);
    }

    /// <summary>Ends the move and returns what was carried and where it was, for the caller to act on.</summary>
    public (CardDto Card, BoardSlot Target)? Release()
    {
        if (Card is null) return null;

        (CardDto, BoardSlot) carried = (Card, slots[index]);
        Card = null;
        return carried;
    }
}

/// <summary>Who is signed in, and what the tenant lets them do.</summary>
public sealed class Session(KanbanApi api)
{
    private MeResponse? me;

    public async Task<MeResponse?> GetMeAsync(CancellationToken cancellationToken) =>
        me ??= (await api.GetMeAsync(cancellationToken)).Value as MeResponse;

    public async Task<bool> MayCreateBoardsAsync(CancellationToken cancellationToken) =>
        await GetMeAsync(cancellationToken) is { } user
        && user.TenantRoles.Any(role => role is Domain.Security.TenantRoles.BoardCreator or Domain.Security.TenantRoles.Admin);
}
