using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Tests.Flow;

public class WipLimitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_limit_outside_1_to_99_is_refused(int maxItems) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WipLimit(maxItems));

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void The_edges_of_the_range_are_legal(int maxItems) =>
        Assert.Equal(maxItems, new WipLimit(maxItems).MaxItems);

    [Fact]
    public void An_unknown_policy_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WipLimit(3, (WipPolicy)9));

    [Fact]
    public void Strict_is_the_default_policy() =>
        Assert.Equal(WipPolicy.Strict, new WipLimit(3).Policy);

    [Fact]
    public void A_limit_allows_counts_up_to_and_including_itself()
    {
        var limit = new WipLimit(5);

        Assert.True(limit.Allows(5));
        Assert.False(limit.Allows(6));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(42, 42)]
    [InlineData(150, 99)]
    public void Clamp_brings_a_suggestion_into_range(int suggested, int expected) =>
        Assert.Equal(expected, WipLimit.Clamp(suggested));
}
