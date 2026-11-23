namespace KanbanHub.Domain.Flow;

/// <summary>The ordered steps cards flow through on a board.</summary>
public sealed class Workflow(IEnumerable<WorkflowStep> orderedSteps)
{
    private readonly List<WorkflowStep> steps = [.. orderedSteps];

    /// <summary>The position of a card that already sits on the board.</summary>
    /// <exception cref="InvalidOperationException">The card is in a step this workflow doesn't have.</exception>
    public CardPosition PositionOf(Location location, Guid? stepId, Guid? parkedFromStepId)
    {
        if (!location.IsInsideStep()) return CardPosition.OutsideSteps(location, parkedFromStepId);

        int order = OrderOf(stepId);
        return order > 0
            ? CardPosition.InStep(stepId!.Value, order, location)
            : throw new InvalidOperationException($"Step {stepId} is not part of the card's workflow.");
    }

    /// <summary>Where a requested move would land, or null when this workflow has no such column.</summary>
    public CardPosition? ResolveTarget(Guid? stepId, Location location)
    {
        if (!location.IsInsideStep()) return CardPosition.OutsideSteps(location);

        int order = OrderOf(stepId);
        bool columnExists = order > 0 && (location is not Location.Track || steps[order - 1].HasTrack);
        return columnExists ? CardPosition.InStep(stepId!.Value, order, location) : null;
    }

    private int OrderOf(Guid? stepId) => steps.FindIndex(step => step.Id == stepId) + 1;
}
