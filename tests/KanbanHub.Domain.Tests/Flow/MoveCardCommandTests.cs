using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Tests.Flow;

public class MoveCardCommandTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Customer hotfix ", "Customer hotfix")]
    public void A_blank_reason_is_no_reason_and_a_real_one_is_trimmed(string? given, string? expected)
    {
        var command = new MoveCardCommand
        {
            CardId = Guid.NewGuid(),
            ExpectedRowVersion = [],
            ToLocation = Location.Parked,
            IdempotencyKey = Guid.NewGuid(),
            Reason = given
        };

        Assert.Equal(expected, command.Reason);
    }
}
