namespace KanbanHub.Domain.Flow;

/// <summary>A card together with the workflow it moves through. Plans moves without touching storage.</summary>
public sealed class CardOnBoard(Guid cardId, Guid boardId, CardPosition position, Workflow workflow)
{
    public Guid CardId { get; } = cardId;

    public Guid BoardId { get; } = boardId;

    public CardPosition Position { get; } = position;

    /// <summary>Plans a move to the given column, or returns null when the board has no such column.</summary>
    public MovePlan? PlanMoveTo(Guid? stepId, Location location) =>
        workflow.ResolveTarget(stepId, location) is { } target ? new MovePlan(Position, target) : null;
}
