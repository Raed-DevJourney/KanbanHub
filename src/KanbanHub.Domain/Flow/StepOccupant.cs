namespace KanbanHub.Domain.Flow;

/// <summary>A card in a step, reduced to what WIP counting needs.</summary>
public readonly record struct StepOccupant(Guid CardId, Guid? ParentCardId, Location Location)
{
    /// <summary>A parent and its children share one family (rule R2).</summary>
    public Guid FamilyId => ParentCardId ?? CardId;
}
