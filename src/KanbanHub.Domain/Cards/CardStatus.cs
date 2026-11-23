using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Cards;

/// <summary>
/// A card's status signals, in the precedence of specification section 6.1: the first that applies
/// colors the card's border and the rest show as badges. Each maps to the palette token <c>status.{name}</c>.
/// </summary>
public enum CardStatus
{
    Canceled,
    Blocked,
    Late,
    AtRisk,
    Tracked,
    Parked,
    InProgress,
    WaitingForPull,
    NotStarted,
    Done
}

/// <summary>What a status is derived from: where the card is and the flags on it.</summary>
public readonly record struct CardSituation(Location Location, bool IsInFinalStep, bool IsBlocked, DateOnly? DueDate);

public static class CardStatuses
{
    /// <summary>
    /// Every status that applies, highest precedence first. "At risk" needs the forecast and
    /// cycle-time percentiles and is not derived yet.
    /// </summary>
    public static IReadOnlyList<CardStatus> Of(CardSituation card, DateOnly today)
    {
        bool finished = card.Location is Location.Done && card.IsInFinalStep;
        bool closed = finished || card.Location is Location.Canceled;

        List<CardStatus> statuses = [];
        if (card.Location is Location.Canceled) statuses.Add(CardStatus.Canceled);
        if (card.IsBlocked) statuses.Add(CardStatus.Blocked);
        if (card.DueDate < today && !closed) statuses.Add(CardStatus.Late);
        statuses.Add(ByPosition(card.Location, finished));
        return [.. statuses.Distinct()];
    }

    /// <summary>The palette token of a status, for example <c>status.waitingForPull</c>.</summary>
    public static string TokenKey(this CardStatus status)
    {
        string name = status.ToString();
        return $"status.{char.ToLowerInvariant(name[0])}{name[1..]}";
    }

    private static CardStatus ByPosition(Location location, bool finished) => location switch
    {
        Location.Canceled => CardStatus.Canceled,
        Location.Track => CardStatus.Tracked,
        Location.Parked => CardStatus.Parked,
        Location.Active => CardStatus.InProgress,
        Location.Done => finished ? CardStatus.Done : CardStatus.WaitingForPull,
        _ => CardStatus.NotStarted
    };
}
