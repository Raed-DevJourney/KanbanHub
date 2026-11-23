using System.Security.Claims;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

public class CancelCardHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly CardOnBoard card = CardAt(Active(Implement));
    private MoveRequest? received;

    [Fact]
    public async Task Canceling_needs_the_cancel_permission()
    {
        MoveOutcome outcome = await CancelAsync(Permission.BoardRead | Permission.CardMove, reason: "Duplicate");

        Assert.Equal(new Forbidden(Permission.CardCancel), outcome.Value);
        Assert.Null(received);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Canceling_needs_a_reason(string? reason)
    {
        MoveOutcome outcome = await CancelAsync(Permission.CardCancel, reason);

        Assert.Equal(new IllegalTransition(CancelCardHandler.ReasonRequired), outcome.Value);
    }

    [Fact]
    public async Task A_cancellation_is_a_move_to_canceled_that_carries_the_reason()
    {
        MoveOutcome outcome = await CancelAsync(Permission.CardCancel, reason: " Duplicate of #12 ");

        Assert.IsType<Moved>(outcome.Value);
        Assert.Equal(CardPosition.OutsideSteps(Location.Canceled), received?.To);
        Assert.Equal(("Duplicate of #12", UserId, MoveSource.User), (received?.Reason, received?.ActorId, received?.Source));
        Assert.Null(received?.OverrideReason);
    }

    private Task<MoveOutcome> CancelAsync(Permission granted, string? reason)
    {
        var handler = new CancelCardHandler(new Repository(this), new Authorizer(granted), TimeProvider.System);
        var command = new CancelCardCommand
        {
            CardId = card.CardId,
            ExpectedRowVersion = [1],
            Reason = reason,
            IdempotencyKey = Guid.NewGuid()
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", UserId.ToString())], "test"));

        return handler.HandleAsync(command, user, TestContext.Current.CancellationToken);
    }

    private sealed class Repository(CancelCardHandlerTests test) : ICardRepository
    {
        public Task<CardOnBoard> GetOnBoardAsync(Guid cardId, CancellationToken cancellationToken) =>
            Task.FromResult(test.card);

        public Task<Moved?> FindAppliedMoveAsync(Guid cardId, Guid idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult<Moved?>(null);

        public Task<MoveOutcome> MoveAsync(MoveRequest request, CancellationToken cancellationToken)
        {
            test.received = request;
            return Task.FromResult<MoveOutcome>(new Moved(request.CardId, test.card.BoardId, 1, [], []));
        }
    }

    private sealed class Authorizer(Permission granted) : IBoardAuthorizer
    {
        public Task<Permission> GetEffectiveAsync(ClaimsPrincipal user, Guid boardId, CancellationToken cancellationToken) =>
            Task.FromResult(granted);
    }
}
