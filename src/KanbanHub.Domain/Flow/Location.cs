namespace KanbanHub.Domain.Flow;

/// <summary>
/// Where a card sits on a board. The values are stored in <c>kb.Card.Location</c>, so never renumber them.
/// </summary>
public enum Location : byte
{
    Backlog = 0,
    Active = 1,
    Done = 2,
    Track = 3,
    Parked = 4,
    Canceled = 5,
    Rollup = 6
}

public static class LocationExtensions
{
    /// <summary>True for the sub-columns that belong to a workflow step.</summary>
    public static bool IsInsideStep(this Location location) =>
        location is Location.Active or Location.Done or Location.Track;
}
