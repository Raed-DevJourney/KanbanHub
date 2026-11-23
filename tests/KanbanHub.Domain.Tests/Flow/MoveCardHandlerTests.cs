using System.Security.Claims;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using static KanbanHub.Domain.Tests.Flow.QuickStart;

namespace KanbanHub.Domain.Tests.Flow;

public class MoveCardHandlerTests
{
    private const Permission Member = Permission.BoardRead | Permission.CardMove;
    private const Permission FlowManager = Member | Permission.CardMoveBackward | Permission.WipOverride;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

    private readonly CardOnBoard card = CardAt(Done(Specify));
    private readonly RecordingCardRepository repository;

    public MoveCardHandlerTests() => repository = new RecordingCardRepository(card);

    [Fact]
    public async Task Without_the_move_permission_the_move_is_forbidden()
    {
        MoveOutcome outcome = await MoveAsync(Permission.BoardRead, To(Implement, Location.Active));

        Assert.Equal(new Forbidden(Permission.CardMove), outcome.Value);
        Assert.Null(repository.Received);
    }

    [Fact]
    public async Task A_legal_pull_reaches_the_repository_with_who_when_and_where()
    {
        MoveCardCommand command = To(Implement, Location.Active);

        await MoveAsync(Member, command);

        MoveRequest request = Assert.IsType<MoveRequest>(repository.Received);
        Assert.Equal(card.CardId, request.CardId);
        Assert.Equal(Active(Implement), request.To);
        Assert.Equal(UserId, request.ActorId);
        Assert.Equal(Now, request.Now);
        Assert.Equal(MoveSource.User, request.Source);
        Assert.Equal(command.IdempotencyKey, request.IdempotencyKey);
        Assert.Same(command.ExpectedRowVersion, request.ExpectedRowVersion);
        Assert.Equal(command.ToRank, request.ToRank);
    }

    [Fact]
    public async Task The_repositorys_outcome_is_returned_unchanged()
    {
        var full = new WipLimitReached([new WipViolation(WipScope.Step, "Implement", 5, 5, WipPolicy.Strict)], []);
        repository.Outcome = full;

        MoveOutcome outcome = await MoveAsync(Member, To(Implement, Location.Active));

        Assert.Same(full, outcome.Value);
    }

    [Fact]
    public async Task A_column_the_board_lacks_is_an_illegal_transition()
    {
        MoveCardCommand command = To(Implement, Location.Active) with { ToStepId = Guid.NewGuid() };

        MoveOutcome outcome = await MoveAsync(Member, command);

        Assert.Equal(IllegalTransition.UnknownColumn, outcome.Value);
        Assert.Null(repository.Received);
    }

    [Fact]
    public async Task A_move_the_flow_rules_forbid_never_reaches_the_repository()
    {
        MoveOutcome outcome = await MoveAsync(FlowManager, To(Validate, Location.Done));

        Assert.IsType<IllegalTransition>(outcome.Value);
        Assert.Null(repository.Received);
    }

    [Fact]
    public async Task A_backward_move_without_the_backward_permission_is_forbidden()
    {
        MoveCardCommand rework = To(Specify, Location.Active) with { Reason = "Spec was wrong" };

        MoveOutcome outcome = await MoveAsync(Member, rework);

        Assert.Equal(new Forbidden(Permission.CardMoveBackward), outcome.Value);
    }

    [Fact]
    public async Task A_backward_move_without_a_reason_is_illegal()
    {
        MoveCardCommand rework = To(Specify, Location.Active) with { Reason = "   " };

        MoveOutcome outcome = await MoveAsync(FlowManager, rework);

        Assert.Equal(new IllegalTransition(MovePlan.ReworkNeedsReason), outcome.Value);
    }

    [Fact]
    public async Task A_backward_move_records_its_reason_as_rework()
    {
        MoveCardCommand rework = To(Specify, Location.Active) with { Reason = " Spec was wrong " };

        await MoveAsync(Member | Permission.CardMoveBackward, rework);

        Assert.Equal("Spec was wrong", repository.Received?.Reason);
        Assert.Null(repository.Received?.OverrideReason);
    }

    [Fact]
    public async Task A_reason_overrides_wip_only_for_callers_who_may_override()
    {
        MoveCardCommand command = To(Implement, Location.Active) with { Reason = "Customer hotfix" };

        await MoveAsync(Member, command);
        Assert.Null(repository.Received?.OverrideReason);

        await MoveAsync(FlowManager, command);
        Assert.Equal("Customer hotfix", repository.Received?.OverrideReason);
        Assert.Null(repository.Received?.Reason);
    }

    [Fact]
    public async Task A_retried_move_gets_the_original_result_even_though_the_card_has_moved_on()
    {
        var original = new Moved(card.CardId, card.BoardId, MovementId: 7, RowVersion: [], Recorded: []);
        repository.AlreadyApplied = original;

        MoveOutcome outcome = await MoveAsync(Member, To(Validate, Location.Done));   // illegal if judged afresh

        Assert.Same(original, outcome.Value);
        Assert.Null(repository.Received);
    }

    [Fact]
    public async Task A_retry_is_not_answered_for_someone_who_may_not_move_cards()
    {
        repository.AlreadyApplied = new Moved(card.CardId, card.BoardId, MovementId: 7, RowVersion: [], Recorded: []);

        MoveOutcome outcome = await MoveAsync(Permission.BoardRead, To(Implement, Location.Active));

        Assert.IsType<Forbidden>(outcome.Value);
    }

    private Task<MoveOutcome> MoveAsync(Permission granted, MoveCardCommand command)
    {
        var handler = new MoveCardHandler(repository, new FixedAuthorizer(granted), new FixedClock(Now));
        return handler.HandleAsync(command, SignedInUser(), TestContext.Current.CancellationToken);
    }

    private MoveCardCommand To(WorkflowStep step, Location location) => new()
    {
        CardId = card.CardId,
        ExpectedRowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        ToStepId = step.Id,
        ToLocation = location,
        ToRank = 1.5m,
        IdempotencyKey = Guid.NewGuid()
    };

    private static ClaimsPrincipal SignedInUser() =>
        new(new ClaimsIdentity([new Claim("oid", UserId.ToString())], authenticationType: "test"));

    private sealed class RecordingCardRepository(CardOnBoard card) : ICardRepository
    {
        public MoveRequest? Received { get; private set; }

        public MoveOutcome Outcome { get; set; } = new Moved(card.CardId, card.BoardId, MovementId: 1, RowVersion: [], Recorded: []);

        public Moved? AlreadyApplied { get; set; }

        public Task<CardOnBoard> GetOnBoardAsync(Guid cardId, CancellationToken cancellationToken) =>
            Task.FromResult(card);

        public Task<Moved?> FindAppliedMoveAsync(Guid cardId, Guid idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(AlreadyApplied);

        public Task<MoveOutcome> MoveAsync(MoveRequest request, CancellationToken cancellationToken)
        {
            Received = request;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FixedAuthorizer(Permission granted) : IBoardAuthorizer
    {
        public Task<Permission> GetEffectiveAsync(ClaimsPrincipal user, Guid boardId, CancellationToken cancellationToken) =>
            Task.FromResult(granted);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
