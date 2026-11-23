using System.Globalization;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;

namespace KanbanHub.Domain.Ai;

/// <summary>
/// A card that has been in flow longer than its service level expectation: the time within which 85% of the
/// cards of its type on its board finished (section 11.1, feature 4).
/// </summary>
public sealed record class AgingCard(Guid CardId, Guid BoardId, int Number, string Title, string CardType, string Step)
{
    /// <summary>Days since the card first entered an Active column.</summary>
    public decimal AgeDays { get; init; }

    /// <summary>The 85th percentile of the cycle time of finished cards of this type.</summary>
    public decimal ExpectedDays { get; init; }

    /// <summary>How many finished cards the expectation is drawn from.</summary>
    public int FinishedCards { get; init; }

    public bool IsBlocked { get; init; }
}

/// <summary>The rule itself: what is compared, over which history, and how the breach is put into words.</summary>
public static class AgingRule
{
    public const int Percentile = 85;

    /// <summary>Fewer finished cards of a type than this say too little to hold a card against.</summary>
    public const int MinFinishedCards = 5;

    public const int WindowDays = 90;

    public static string Describe(AgingCard card) => string.Create(CultureInfo.InvariantCulture,
        $"In flow for {card.AgeDays:0.#} days. {Percentile}% of {card.CardType} cards on this board finish within {card.ExpectedDays:0.#}.");
}

/// <summary>Storage port for the aging rule. The comparison is done in SQL, over every board at once.</summary>
public interface IAgingStore
{
    /// <summary>Cards past their expectation that nobody was told about yet.</summary>
    Task<IReadOnlyList<AgingCard>> ListNewBreachesAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Remembers that the card's alert was raised, so it is raised once.</summary>
    Task MarkAlertedAsync(AgingCard card, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record class AgingExplanation(string Explanation);

/// <summary>The R2 part of feature 4: on boards that use AI, the model words the alert. The rule decided it.</summary>
public sealed class AgingExplanationTask : AiTask<AgingCard, AgingExplanation>
{
    public override AiFeature Feature => AiFeature.AgingExplanation;

    public override string Instructions =>
        "A Kanban card has been in flow longer than its service level expectation: expectedDays is the time within which"
        + " 85% of finished cards of its type finished, drawn from finishedCards cards. In two short sentences, say how long the"
        + " card has been in flow against that, and suggest one thing its team could check (is it blocked, too big, waiting?)."
        + GroundRules;

    public override bool IsGrounded(AgingCard data, AgingExplanation result) =>
        result.Explanation is { Length: > 0 and <= NotificationPlanner.MaxDetailLength }
        && Grounding.MentionsOnly(result.Explanation, [data.Number])
        && Grounding.NumbersComeFrom(result.Explanation, [data.AgeDays, data.ExpectedDays, data.FinishedCards, AgingRule.Percentile]);
}

/// <summary>
/// Raises aging alerts (feature 4). The rule runs for every board; who is told follows the notification rules
/// and each person's choices. Where the board uses AI the model words the alert, and where it doesn't, or its
/// answer fails the grounding check, the rule's own sentence is used. Each card is alerted once.
/// </summary>
public sealed class AgingAlerts(
    IAgingStore aging, INotificationStore audience, NotificationSender sender, AiRunner runner, TimeProvider clock)
{
    private const string EventType = "card.aging";

    /// <summary>Returns how many cards were alerted.</summary>
    public async Task<int> RaiseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgingCard> breaches = await aging.ListNewBreachesAsync(clock.GetUtcNow(), cancellationToken);
        foreach (AgingCard card in breaches)
            await AlertAsync(card, cancellationToken);

        return breaches.Count;
    }

    private async Task AlertAsync(AgingCard card, CancellationToken cancellationToken)
    {
        // The people around a card are looked up the way they are for an event about it; this one has no outbox row.
        var about = new BoardEvent(Id: 0, card.BoardId, card.CardId, EventType, Payload: "{}");
        if (await audience.FindContextAsync(about, cancellationToken) is { } people)
        {
            var subject = new NotificationSubject(card.BoardId, card.CardId, $"aging:{card.CardId}");
            string detail = await ExplainAsync(card, cancellationToken);
            await sender.SendAsync(subject, NotificationPlanner.PlanAging(people, detail), cancellationToken);
        }

        await aging.MarkAlertedAsync(card, clock.GetUtcNow(), cancellationToken);
    }

    private async Task<string> ExplainAsync(AgingCard card, CancellationToken cancellationToken)
    {
        AiResult<AgingExplanation> explained = await runner.RunAsync(card.BoardId, new AgingExplanationTask(), card, cancellationToken);
        return explained.Value is AgingExplanation written ? written.Explanation : AgingRule.Describe(card);
    }
}
