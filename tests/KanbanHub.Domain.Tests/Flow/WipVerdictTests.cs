using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Tests.Flow;

public class WipVerdictTests
{
    [Fact]
    public void No_violation_is_allowed() =>
        Assert.Equal(WipVerdict.Allowed, WipVerdicts.Judge([], hasOverrideReason: false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_strict_violation_rejects_even_with_a_reason(bool hasOverrideReason) =>
        Assert.Equal(WipVerdict.Rejected, WipVerdicts.Judge([Violation(WipPolicy.Strict)], hasOverrideReason));

    [Fact]
    public void An_allow_with_reason_violation_needs_a_reason()
    {
        WipViolation[] violations = [Violation(WipPolicy.AllowWithReason)];

        Assert.Equal(WipVerdict.ReasonRequired, WipVerdicts.Judge(violations, hasOverrideReason: false));
        Assert.Equal(WipVerdict.Allowed, WipVerdicts.Judge(violations, hasOverrideReason: true));
    }

    [Fact]
    public void A_warn_violation_never_stops_a_move() =>
        Assert.Equal(WipVerdict.Allowed, WipVerdicts.Judge([Violation(WipPolicy.Warn)], hasOverrideReason: false));

    [Fact]
    public void The_strictest_violation_decides()
    {
        WipViolation[] violations = [Violation(WipPolicy.Warn), Violation(WipPolicy.AllowWithReason), Violation(WipPolicy.Strict)];

        Assert.Equal(WipVerdict.Rejected, WipVerdicts.Judge(violations, hasOverrideReason: true));
    }

    private static WipViolation Violation(WipPolicy policy) => new(WipScope.Step, "Validate", Limit: 3, Current: 3, policy);
}
