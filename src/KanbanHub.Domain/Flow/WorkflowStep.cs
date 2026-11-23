namespace KanbanHub.Domain.Flow;

/// <summary>One step of a board's workflow, with the flags that change how its WIP is counted.</summary>
public sealed record class WorkflowStep(Guid Id, string Name)
{
    /// <summary>The step offers a Track sub-column for work waiting on outside input.</summary>
    public bool HasTrack { get; init; }

    /// <summary>The last step: its Done sub-column is off the board for WIP purposes.</summary>
    public bool IsFinal { get; init; }

    /// <summary>The breakdown step: a parent and its children occupy one WIP slot together.</summary>
    public bool CountsParentsOnly { get; init; }
}
