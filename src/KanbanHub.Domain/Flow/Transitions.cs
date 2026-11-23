namespace KanbanHub.Domain.Flow;

/// <summary>The legal card moves of specification section 5.3 (FR-M01 and FR-M02).</summary>
public static class Transitions
{
    /// <summary>Returns why a move is illegal, or null when the board allows it.</summary>
    public static string? Check(CardPosition from, CardPosition to, bool canMoveBackward) =>
        (from.Location, to.Location) switch
        {
            (Location.Backlog, Location.Active) when to.StepOrder == 1 => null,
            (Location.Active, Location.Done) when to.StepId == from.StepId => null,
            (Location.Done, Location.Active) when to.StepOrder == from.StepOrder + 1 => null,
            (Location.Active, Location.Track) or (Location.Track, Location.Active)
                when to.StepId == from.StepId => null,
            (_, Location.Parked) => null,
            (Location.Parked, Location.Backlog) => null,
            (Location.Parked, Location.Active) when to.StepId == from.ParkedFromStepId => null,
            (Location.Done or Location.Active, Location.Active)
                when canMoveBackward && to.StepOrder <= from.StepOrder => null,
            _ => $"A move from {from.Location} to {to.Location} isn't allowed on this board."
        };
}
