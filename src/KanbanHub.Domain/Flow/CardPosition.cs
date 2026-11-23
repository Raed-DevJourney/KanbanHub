namespace KanbanHub.Domain.Flow;

/// <summary>A card's place on the board: a location and, inside a step, which step.</summary>
public sealed record class CardPosition
{
    private CardPosition(Location location) => Location = location;

    public Location Location { get; }

    public Guid? StepId { get; private init; }

    /// <summary>1-based position of the step in its workflow; 0 outside a step.</summary>
    public int StepOrder { get; private init; }

    /// <summary>For a parked card, the step it was parked from (null when it came from the backlog).</summary>
    public Guid? ParkedFromStepId { get; private init; }

    public static CardPosition Backlog { get; } = new(Location.Backlog);

    public static CardPosition InStep(Guid stepId, int stepOrder, Location location)
    {
        if (!location.IsInsideStep())
            throw new ArgumentException($"{location} is not a sub-column of a step.", nameof(location));
        ArgumentOutOfRangeException.ThrowIfLessThan(stepOrder, 1);

        return new CardPosition(location) { StepId = stepId, StepOrder = stepOrder };
    }

    public static CardPosition OutsideSteps(Location location, Guid? parkedFromStepId = null)
    {
        if (location.IsInsideStep())
            throw new ArgumentException($"{location} needs a step.", nameof(location));

        return new CardPosition(location) { ParkedFromStepId = parkedFromStepId };
    }

    /// <summary>
    /// Rework: pulling started work into Active at an earlier step, or back out of its own step's Done.
    /// </summary>
    public bool IsReworkedBy(CardPosition target) =>
        Location is Location.Active or Location.Done
        && target.Location is Location.Active
        && (target.StepOrder < StepOrder || (target.StepOrder == StepOrder && Location is Location.Done));
}
