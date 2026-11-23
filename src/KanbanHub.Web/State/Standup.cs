using KanbanHub.Contracts;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Web.State;

/// <summary>A card as the standup talks about it.</summary>
public sealed record StandupCard(CardDto Card, int DaysInPlace)
{
    public bool IsAging => DaysInPlace >= StandupWalk.AgingAfterDays;

    public bool NeedsAttention => Card.IsBlocked || IsAging;
}

/// <summary>One stop of the walk: a step and its cards, the ones that need attention first.</summary>
public sealed record StandupStop(StepDto Step, IReadOnlyList<StandupCard> Cards)
{
    /// <summary>How many more cards the step can take, so the team sees where to pull; null without a limit.</summary>
    public int? Room => Step.WipLimit is { } limit ? Math.Max(0, limit - Step.WipCount) : null;

    public int BlockedCount => Cards.Count(card => card.Card.IsBlocked);
}

/// <summary>
/// The daily standup as the book runs it: walk the board from right to left, blocked and aging cards first,
/// and end on the one required question: is anyone blocked?
/// </summary>
public static class StandupWalk
{
    /// <summary>A card that has sat this many days in one place is called out as aging.</summary>
    public const int AgingAfterDays = 3;

    /// <summary>The stops, from the last step to the first. Finished work is not discussed.</summary>
    public static IReadOnlyList<StandupStop> Plan(BoardSnapshotDto board, DateTime nowUtc) =>
    [
        .. board.Steps
            .OrderByDescending(step => step.Order)
            .Select(step => new StandupStop(step, InOrder(board.Cards.Where(card => IsDiscussedAt(step, card)), nowUtc)))
    ];

    /// <summary>Every blocked card on the board, the longest wait first.</summary>
    public static IReadOnlyList<StandupCard> Blocked(BoardSnapshotDto board, DateTime nowUtc) =>
        InOrder(board.Cards.Where(card => card.IsBlocked), nowUtc);

    private static bool IsDiscussedAt(StepDto step, CardDto card) =>
        card.StepId == step.Id && card.Location.IsInsideStep() && !(step.IsFinal && card.Location == Location.Done);

    private static IReadOnlyList<StandupCard> InOrder(IEnumerable<CardDto> cards, DateTime nowUtc) =>
    [
        .. cards
            .Select(card => new StandupCard(card, DaysSince(card.EnteredLocationAtUtc, nowUtc)))
            .OrderByDescending(card => card.Card.IsBlocked)
            .ThenByDescending(card => card.DaysInPlace)
            .ThenBy(card => card.Card.Rank)
    ];

    private static int DaysSince(DateTime thenUtc, DateTime nowUtc) => Math.Max(0, (int)(nowUtc - thenUtc).TotalDays);
}

/// <summary>The standup's 15 minutes. Running over is shown, not enforced: the team decides when it is done.</summary>
public sealed class StandupClock(TimeProvider clock)
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(15);

    private DateTimeOffset? startedAt;

    public bool IsRunning => startedAt is not null;

    /// <summary>Negative once the standup runs over.</summary>
    public TimeSpan Remaining => startedAt is { } start ? Length - (clock.GetUtcNow() - start) : Length;

    public bool IsOver => Remaining < TimeSpan.Zero;

    /// <summary>"12:34" while there is time, "+01:10" once there isn't.</summary>
    public string Display => IsOver ? "+" + Format(Remaining.Negate()) : Format(Remaining);

    public void Start() => startedAt = clock.GetUtcNow();

    public void Reset() => startedAt = null;

    private static string Format(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
}
