namespace KanbanHub.Domain.Flow;

/// <summary>What the limit policies decide about a move (rule R6).</summary>
public enum WipVerdict
{
    Allowed,
    Rejected,
    ReasonRequired
}

public static class WipVerdicts
{
    /// <summary>
    /// A strict violation rejects; an "Allow with reason" violation needs an override reason;
    /// "Warn" violations never stop a move.
    /// </summary>
    public static WipVerdict Judge(IReadOnlyCollection<WipViolation> violations, bool hasOverrideReason)
    {
        if (violations.Any(v => v.Policy is WipPolicy.Strict)) return WipVerdict.Rejected;
        if (violations.Any(v => v.Policy is WipPolicy.AllowWithReason) && !hasOverrideReason) return WipVerdict.ReasonRequired;
        return WipVerdict.Allowed;
    }
}
