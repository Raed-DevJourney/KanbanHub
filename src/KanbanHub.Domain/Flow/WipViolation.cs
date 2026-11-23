namespace KanbanHub.Domain.Flow;

/// <summary>What happens when a move would exceed a limit (rule R6). Stored in <c>kb.WipLimit.Policy</c>.</summary>
public enum WipPolicy : byte
{
    /// <summary>Reject the move.</summary>
    Strict = 1,

    /// <summary>Let holders of <c>wip.override</c> exceed the limit when they give a reason.</summary>
    AllowWithReason = 2,

    /// <summary>Allow the move and record that the limit was exceeded.</summary>
    Warn = 3
}

/// <summary>What a limit applies to (rule R3). Stored in <c>kb.WipLimit.Scope</c>.</summary>
public enum WipScope : byte
{
    Step = 1,
    Lane = 2,
    Cell = 3,
    Person = 4,
    StepGroup = 5
}

/// <summary>A limit that a move would exceed. <paramref name="Current"/> is the count before the move.</summary>
public sealed record class WipViolation(WipScope Scope, string ScopeName, int Limit, int Current, WipPolicy Policy);
