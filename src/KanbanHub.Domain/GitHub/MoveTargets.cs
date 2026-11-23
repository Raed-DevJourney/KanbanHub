using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.GitHub;

/// <summary>A column a change in GitHub asks to move a card to.</summary>
public sealed record class MoveTarget(Guid? StepId, Location Location, string Name);

/// <summary>Turns what GitHub can say ("next", a step label, "done", "track") into a column of the card's board.</summary>
public static class MoveTargets
{
    /// <summary>One column forward along the flow (FR-M01), or null when the card has nowhere further to go.</summary>
    public static MoveTarget? Next(SyncedCard card) => card.Location switch
    {
        Location.Backlog => In(card.Steps.MinBy(step => step.Order), Location.Active),
        Location.Active => In(card.Step, Location.Done),
        Location.Track => In(card.Step, Location.Active),
        Location.Done => In(card.Steps.FirstOrDefault(step => step.Order == card.Step?.Order + 1), Location.Active),
        _ => null
    };

    public static MoveTarget? Done(SyncedCard card) => In(card.Step, Location.Done);

    public static MoveTarget? Blocked(SyncedCard card) =>
        card.Step is { HasBlockedColumn: true } step ? In(step, Location.Track) : null;

    public static MoveTarget? Active(SyncedStep? step) => In(step, Location.Active);

    private static MoveTarget? In(SyncedStep? step, Location location) =>
        step is null ? null : new MoveTarget(step.Id, location, $"{step.Name} {StatusComment.NameOf(location)}");
}

/// <summary>Says in words how a move asked for in GitHub ended, for the reply comment and the card's sync badge.</summary>
public static class MoveReplies
{
    public static string Describe(MoveOutcome outcome, MoveTarget target) => outcome switch
    {
        Moved => $"Moved to {target.Name}.",
        WipLimitReached full => $"Not moved to {target.Name}: {DescribeLimit(full.Violations)}. Help finish a card there first.",
        OverrideReasonRequired over => $"Not moved to {target.Name}: {DescribeLimit(over.Violations)}, and going over it needs a reason given in KanbanHub.",
        DoneRuleUnmet unmet => $"Not moved to {target.Name}: the done rule isn't met yet ({string.Join(", ", unmet.UnmetItems)}).",
        ConcurrencyConflict => $"Not moved to {target.Name}: the card changed at the same moment. Try again.",
        IllegalTransition illegal => $"Not moved to {target.Name}: {illegal.Reason}",
        Forbidden forbidden => $"Not moved to {target.Name}: the GitHub App lacks the {forbidden.Missing} permission on this board.",
    };

    private static string DescribeLimit(IReadOnlyList<WipViolation> violations) =>
        violations is [var first, ..]
            ? $"{first.ScopeName} is at its WIP limit ({first.Current} of {first.Limit})"
            : "a WIP limit is reached";
}
