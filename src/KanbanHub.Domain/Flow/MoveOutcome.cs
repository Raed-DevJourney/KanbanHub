using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Flow;

/// <summary>The move was applied. <paramref name="Recorded"/> lists limits it exceeded by warning or override.</summary>
public sealed record class Moved(
    Guid CardId, Guid BoardId, long MovementId, byte[] RowVersion, IReadOnlyList<WipViolation> Recorded);

/// <summary>A strict limit refused the move. <paramref name="SwarmCandidates"/> are cards to help finish instead.</summary>
public sealed record class WipLimitReached(IReadOnlyList<WipViolation> Violations, IReadOnlyList<Guid> SwarmCandidates);

/// <summary>The limit can be exceeded, but only by a permitted caller who gives a reason.</summary>
public sealed record class OverrideReasonRequired(IReadOnlyList<WipViolation> Violations);

/// <summary>Required done-rule items are still unchecked.</summary>
public sealed record class DoneRuleUnmet(IReadOnlyList<string> UnmetItems);

/// <summary>The card changed since the caller read it.</summary>
public sealed record class ConcurrencyConflict(byte[] CurrentRowVersion);

/// <summary>The board's flow rules don't allow this move.</summary>
public sealed record class IllegalTransition(string Reason)
{
    /// <summary>The target step is not an open column of the card's board.</summary>
    public static IllegalTransition UnknownColumn { get; } = new("That column isn't part of this board's workflow.");
}

/// <summary>The caller lacks a permission the move needs.</summary>
public sealed record class Forbidden(Permission Missing);

/// <summary>
/// Every way a move can end. Being a union, each <c>switch</c> over it must handle every case,
/// so adding an outcome is a compile error until all callers deal with it.
/// </summary>
public union MoveOutcome(Moved, WipLimitReached, OverrideReasonRequired,
                         DoneRuleUnmet, ConcurrencyConflict, IllegalTransition, Forbidden);
