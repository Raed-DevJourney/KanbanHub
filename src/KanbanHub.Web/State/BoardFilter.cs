using KanbanHub.Contracts;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Web.State;

/// <summary>The filter chips above the board (FR-V03): what the person wants to see right now.</summary>
public sealed class BoardFilter
{
    public event Action? Changed;

    public bool BlockedOnly { get; private set; }

    public bool LateOnly { get; private set; }

    /// <summary>Only cards an agent session is open on.</summary>
    public bool AgentOnly { get; private set; }

    public CardType? Type { get; private set; }

    public string Text { get; private set; } = "";

    public bool IsActive => BlockedOnly || LateOnly || AgentOnly || Type is not null || Text.Length > 0;

    public void ToggleBlocked() => Change(() => BlockedOnly = !BlockedOnly);

    public void ToggleLate() => Change(() => LateOnly = !LateOnly);

    public void ToggleAgent() => Change(() => AgentOnly = !AgentOnly);

    public void ShowType(CardType? type) => Change(() => Type = type);

    public void Search(string? text) => Change(() => Text = text?.Trim() ?? "");

    public void Clear() => Change(() => (BlockedOnly, LateOnly, AgentOnly, Type, Text) = (false, false, false, null, ""));

    public bool Matches(CardDto card, DateOnly today) =>
        (!BlockedOnly || card.IsBlocked)
        && (!LateOnly || IsLate(card, today))
        && (!AgentOnly || card.Agent is not null)
        && (Type is null || card.CardType == Type)
        && card.Title.Contains(Text, StringComparison.OrdinalIgnoreCase);

    private static bool IsLate(CardDto card, DateOnly today) =>
        card.DueDate < today && card.Location is not (Location.Canceled or Location.Rollup);

    private void Change(Action change)
    {
        change();
        Changed?.Invoke();
    }
}

/// <summary>Turns one backlog gesture into the whole <see cref="BacklogOrder"/> the API takes.</summary>
public static class BacklogOrdering
{
    /// <summary>The Ready list as it is now, top first.</summary>
    public static List<Guid> ReadyList(IEnumerable<CardDto> cards) =>
        [.. cards.Where(card => card is { Location: Location.Backlog, IsReady: true }).OrderBy(card => card.Rank).Select(card => card.Id)];

    /// <summary>Puts a card at the end of the Ready list, or takes it off.</summary>
    public static BacklogOrder ToggleReady(IEnumerable<CardDto> cards, CardDto card)
    {
        List<Guid> ready = ReadyList(cards);
        if (!ready.Remove(card.Id)) ready.Add(card.Id);
        return new BacklogOrder(ready, []);
    }

    /// <summary>Moves a Ready card up (-1) or down (+1); the ends of the list hold it in place.</summary>
    public static BacklogOrder Shift(IEnumerable<CardDto> cards, CardDto card, int direction)
    {
        List<Guid> ready = ReadyList(cards);
        int from = ready.IndexOf(card.Id);
        int to = Math.Clamp(from + direction, 0, ready.Count - 1);
        if (from >= 0 && to != from) (ready[from], ready[to]) = (ready[to], ready[from]);
        return new BacklogOrder(ready, []);
    }

    public static BacklogOrder SetBucket(IEnumerable<CardDto> cards, CardDto card, byte bucket) =>
        new(ReadyList(cards), [new BucketPlacement(card.Id, bucket)]);
}
