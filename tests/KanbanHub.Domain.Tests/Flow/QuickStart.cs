using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Tests.Flow;

/// <summary>The book's quick-start board: Specify, Implement, Validate.</summary>
internal static class QuickStart
{
    public static readonly WorkflowStep Specify = new(Guid.NewGuid(), "Specify") { CountsParentsOnly = true };
    public static readonly WorkflowStep Implement = new(Guid.NewGuid(), "Implement") { HasTrack = true };
    public static readonly WorkflowStep Validate = new(Guid.NewGuid(), "Validate") { IsFinal = true };

    private static readonly WorkflowStep[] Steps = [Specify, Implement, Validate];

    public static Workflow BoardWorkflow { get; } = new(Steps);

    public static CardPosition Active(WorkflowStep step) => At(step, Location.Active);

    public static CardPosition Done(WorkflowStep step) => At(step, Location.Done);

    public static CardPosition Track(WorkflowStep step) => At(step, Location.Track);

    public static CardPosition ParkedFrom(WorkflowStep? step) => CardPosition.OutsideSteps(Location.Parked, step?.Id);

    public static CardOnBoard CardAt(CardPosition position) =>
        new(Guid.NewGuid(), Guid.NewGuid(), position, BoardWorkflow);

    private static CardPosition At(WorkflowStep step, Location location) =>
        CardPosition.InStep(step.Id, Array.IndexOf(Steps, step) + 1, location);
}
