using System.Security.Claims;
using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;
using KanbanHub.Domain.Security;

namespace KanbanHub.Data.IntegrationTests.MoveCard;

/// <summary>The handler and the real repository together: all gates of section 5, from permission to procedure.</summary>
public class MoveCardHandlerEndToEndTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    private const Permission Member = Permission.BoardRead | Permission.CardMove;

    [Fact]
    public async Task A_member_pulls_a_card_into_the_next_step()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        MoveOutcome outcome = await HandleAsync(Member, card, Board.Implement.Active);

        Assert.IsType<Moved>(outcome.Value);
        Assert.Equal(Board.Implement.Id, (await Records.CardAsync(card)).StepId);
        Assert.Equal(TestBoard.Actor, Assert.Single(await Records.MovementsAsync(card)).ActorId);
    }

    [Fact]
    public async Task A_contributor_cannot_move_cards()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        MoveOutcome outcome = await HandleAsync(Permission.BoardRead | Permission.BacklogCreate, card, Board.Implement.Active);

        Assert.Equal(new Forbidden(Permission.CardMove), outcome.Value);
        Assert.Empty(await Records.MovementsAsync(card));
    }

    [Fact]
    public async Task Skipping_a_step_is_refused_before_the_database_is_asked_to_move()
    {
        Guid card = await Board.AddCardAsync(CardPosition.Backlog);

        MoveOutcome outcome = await HandleAsync(Member, card, Board.Implement.Active);

        Assert.IsType<IllegalTransition>(outcome.Value);
        Assert.Equal(Location.Backlog, (await Records.CardAsync(card)).Location);
    }

    [Fact]
    public async Task A_member_without_the_override_permission_cannot_talk_past_a_limit()
    {
        await Board.SetLimitAsync(Board.Implement, 1, WipPolicy.AllowWithReason);
        await Board.AddCardAsync(Board.Implement.Active);
        Guid card = await Board.AddCardAsync(Board.Specify.Done);

        MoveOutcome outcome = await HandleAsync(Member, card, Board.Implement.Active, reason: "I really want to");

        Assert.IsType<OverrideReasonRequired>(outcome.Value);
    }

    private async Task<MoveOutcome> HandleAsync(Permission granted, Guid cardId, CardPosition to, string? reason = null)
    {
        var handler = new MoveCardHandler(Repository, new FixedAuthorizer(granted), TimeProvider.System);
        var command = new MoveCardCommand
        {
            CardId = cardId,
            ExpectedRowVersion = (await Records.CardAsync(cardId)).RowVersion,
            ToStepId = to.StepId,
            ToLocation = to.Location,
            Reason = reason,
            IdempotencyKey = Guid.NewGuid()
        };

        return await handler.HandleAsync(command, SignedInUser(), TestContext.Current.CancellationToken);
    }

    private static ClaimsPrincipal SignedInUser() =>
        new(new ClaimsIdentity([new Claim("oid", TestBoard.Actor.ToString())], authenticationType: "test"));

    private sealed class FixedAuthorizer(Permission granted) : IBoardAuthorizer
    {
        public Task<Permission> GetEffectiveAsync(ClaimsPrincipal user, Guid boardId, CancellationToken cancellationToken) =>
            Task.FromResult(granted);
    }
}
