using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Flow;

/// <summary>A requested move between two positions, and what the flow rules demand of it.</summary>
public sealed record class MovePlan(CardPosition From, CardPosition To)
{
    public const string ReworkNeedsReason = "Moving a card backward needs a reason.";

    public bool IsBackward => From.IsReworkedBy(To);

    /// <summary>The permission this move needs on top of <see cref="Permission.CardMove"/>, when the caller lacks it.</summary>
    public Permission? FindMissingPermission(Permission granted) =>
        IsBackward && !granted.HasFlag(Permission.CardMoveBackward) ? Permission.CardMoveBackward : null;

    /// <summary>Returns why the board's rules forbid this move, or null when it is legal (FR-M01, FR-M02).</summary>
    public string? FindIllegality(bool hasReason) =>
        Transitions.Check(From, To, canMoveBackward: IsBackward)
        ?? (IsBackward && !hasReason ? ReworkNeedsReason : null);
}
