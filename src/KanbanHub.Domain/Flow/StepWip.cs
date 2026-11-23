namespace KanbanHub.Domain.Flow;

/// <summary>
/// A step's work in progress, counted by rules R1 and R2. This is the readable model of what
/// <c>kb.tvf_StepWipViolations</c> enforces in the database; the server stays the authority.
/// </summary>
public sealed class StepWip(WorkflowStep step, IEnumerable<StepOccupant> occupants)
{
    private readonly List<StepOccupant> counted = [.. occupants.Where(o => Counts(step, o.Location))];

    public int Count => CountUnits(counted);

    /// <summary>The count once <paramref name="incoming"/> sits in Active (a card joining its family adds nothing).</summary>
    public int CountAfterPulling(StepOccupant incoming) =>
        CountUnits(Others(incoming).Append(incoming with { Location = Location.Active }));

    /// <summary>The violation that pulling <paramref name="incoming"/> into Active would cause, or null when it fits.</summary>
    public WipViolation? FindViolation(WipLimit limit, StepOccupant incoming) =>
        limit.Allows(CountAfterPulling(incoming))
            ? null
            : new WipViolation(WipScope.Step, step.Name, limit.MaxItems, CountUnits(Others(incoming)), limit.Policy);

    /// <summary>R1: Active always counts; Done counts unless the step is final. Nothing else does.</summary>
    private static bool Counts(WorkflowStep step, Location location) =>
        location is Location.Active || (location is Location.Done && !step.IsFinal);

    private IEnumerable<StepOccupant> Others(StepOccupant card) => counted.Where(o => o.CardId != card.CardId);

    private int CountUnits(IEnumerable<StepOccupant> cards) => cards.Select(UnitOf).Distinct().Count();

    /// <summary>R2: where parents only count, a whole family is one unit; elsewhere each card is.</summary>
    private Guid UnitOf(StepOccupant card) => step.CountsParentsOnly ? card.FamilyId : card.CardId;
}
