using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>Moving and canceling cards over HTTP: headers, status codes and what the board shows afterwards.</summary>
public class CardFlowTests(KanbanDatabase database)
{
    [Fact]
    public async Task A_pull_answers_200_with_the_new_etag_and_shows_on_the_board()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();
        HttpRequest request = Requests.From(
            board.Owner, new MoveCardBody(board.Specify.Id, Location.Active), await board.ETagOfAsync(card), Guid.NewGuid());

        IActionResult result = await board.Flow.Move(request, card, Ct);

        Assert.Equal(board.BoardId, result.ValueOf<Moved>().BoardId);
        Assert.Equal($"\"{await board.ETagOfAsync(card)}\"", request.HttpContext.Response.Headers.ETag.ToString());
        Assert.Equal(1, (await board.SnapshotAsync()).Steps[0].WipCount);
    }

    [Fact]
    public async Task A_move_needs_an_idempotency_key_an_etag_and_a_body()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();
        var body = new MoveCardBody(board.Specify.Id, Location.Active);
        string etag = await board.ETagOfAsync(card);

        (await board.Flow.Move(Requests.From(board.Owner, body, etag), card, Ct)).ProblemOf(400, "idempotency-key-required");
        (await board.Flow.Move(Requests.From(board.Owner, body, idempotencyKey: Guid.NewGuid()), card, Ct)).ProblemOf(428, "if-match-required");
        (await board.Flow.Move(Requests.From(board.Owner, body: null, etag, Guid.NewGuid()), card, Ct)).ProblemOf(400, "body-required");
    }

    [Fact]
    public async Task A_pull_into_a_full_step_is_a_409_naming_cards_to_help_finish()
    {
        BoardScenario board = await StartAsync(database);
        Guid[] inSpecify = [await board.AddCardAtAsync(board.Specify, Location.Active), await board.AddCardAtAsync(board.Specify, Location.Done)];
        Guid waiting = await board.AddCardAsync();

        IActionResult result = await board.MoveAsync(board.Owner, waiting, board.Specify, Location.Active);

        ProblemDetails problem = result.ProblemOf(409, "wip-limit-reached");
        Assert.Equal("Specify is at 2 of 2. Help finish a card in Specify before starting another.", problem.Detail);
        Assert.Equivalent(inSpecify, problem.Extensions["swarmCandidates"]);
        Assert.Equal(Location.Backlog, (await board.CardAsync(waiting)).Card.Location);
    }

    [Fact]
    public async Task A_flow_manager_exceeds_an_allow_with_reason_limit_by_giving_one()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal flowManager = await board.UserWithAsync(BuiltInRoles.FlowManager);
        await board.SetLimitsAsync(board.Owner, new StepLimit(board.Specify.Id, 1, WipPolicy.AllowWithReason));
        await board.AddCardAtAsync(board.Specify, Location.Active);
        Guid hotfix = await board.AddCardAsync();

        IActionResult withoutReason = await board.MoveAsync(flowManager, hotfix, board.Specify, Location.Active);
        IActionResult withReason = await board.MoveAsync(flowManager, hotfix, board.Specify, Location.Active, reason: "Customer hotfix");

        withoutReason.ProblemOf(428, "override-reason-required");
        Assert.Single(withReason.ValueOf<Moved>().Recorded);
    }

    [Fact]
    public async Task A_stale_etag_is_a_412_and_the_card_stays_put()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();
        string staleETag = await board.ETagOfAsync(card);
        await board.MustMoveAsync(card, board.Specify, Location.Active);
        var body = new MoveCardBody(board.Specify.Id, Location.Done);

        IActionResult result = await board.Flow.Move(Requests.From(board.Owner, body, staleETag, Guid.NewGuid()), card, Ct);

        Assert.Equal($"\"{await board.ETagOfAsync(card)}\"", result.ProblemOf(412, "precondition-failed").Extensions["currentETag"]);
    }

    [Fact]
    public async Task A_retried_move_answers_as_the_first_one_did()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();
        var body = new MoveCardBody(board.Specify.Id, Location.Active);
        string etag = await board.ETagOfAsync(card);
        Guid key = Guid.NewGuid();

        var first = (await board.Flow.Move(Requests.From(board.Owner, body, etag, key), card, Ct)).ValueOf<Moved>();
        var retry = (await board.Flow.Move(Requests.From(board.Owner, body, etag, key), card, Ct)).ValueOf<Moved>();

        Assert.Equal(first.MovementId, retry.MovementId);
    }

    [Fact]
    public async Task Finishing_a_step_with_an_unmet_done_rule_is_a_422_listing_it()
    {
        BoardScenario board = await StartAsync(database);
        var rule = new SetDoneRuleRequest([new("Code reviewed", IsRequired: true), new("Unit tests pass", IsRequired: true)]);
        await board.Settings.SetDoneRule(Requests.From(board.Owner, rule), board.BoardId, board.Specify.Id, Ct);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);
        Guid reviewed = (await board.CardAsync(card)).Checklist[0].Id;
        await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, reviewed, Ct);

        IActionResult result = await board.MoveAsync(board.Owner, card, board.Specify, Location.Done);

        Assert.Equal(["Unit tests pass"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(result.ProblemOf(422, "done-rule-unmet").Extensions["unmetItems"]));
    }

    [Fact]
    public async Task Skipping_a_step_is_a_422()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();

        (await board.MoveAsync(board.Owner, card, board.Implement, Location.Active)).ProblemOf(422, "illegal-transition");
    }

    [Fact]
    public async Task Moving_a_card_that_does_not_exist_is_a_404()
    {
        BoardScenario board = await StartAsync(database);
        HttpRequest request = Requests.From(
            board.Owner, new MoveCardBody(board.Specify.Id, Location.Active), "AAAAAAAAAAE=", Guid.NewGuid());

        (await board.Flow.Move(request, Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task Moving_backward_takes_the_permission_and_a_reason()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal member = await board.UserWithAsync(BuiltInRoles.Member);
        Guid card = await board.AddCardAtAsync(board.Implement, Location.Active);

        IActionResult byMember = await board.MoveAsync(member, card, board.Specify, Location.Active, reason: "Spec was wrong");
        IActionResult withoutReason = await board.MoveAsync(board.Owner, card, board.Specify, Location.Active);
        IActionResult withReason = await board.MoveAsync(board.Owner, card, board.Specify, Location.Active, reason: "Spec was wrong");

        Assert.Equal("Missing permission: CardMoveBackward.", byMember.ProblemOf(403, "forbidden").Detail);
        withoutReason.ProblemOf(422, "illegal-transition");
        Assert.Equal(200, withReason.StatusOf());
    }

    [Fact]
    public async Task Canceling_needs_a_reason_then_frees_the_slot_and_takes_the_card_off_the_board()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);

        IActionResult withoutReason = await CancelAsync(board, card, reason: " ");
        IActionResult canceled = await CancelAsync(board, card, reason: "Duplicate of #12");

        withoutReason.ProblemOf(422, "illegal-transition");
        Assert.Equal(200, canceled.StatusOf());
        BoardSnapshotDto snapshot = await board.SnapshotAsync();
        Assert.Equal((0, 0), (snapshot.Steps[0].WipCount, snapshot.Cards.Count));
        CardDetailDto kept = await board.CardAsync(card);
        Assert.Equal((Location.Canceled, "Duplicate of #12"), (kept.Card.Location, kept.CanceledReason));
    }

    private static async Task<IActionResult> CancelAsync(BoardScenario board, Guid cardId, string reason) =>
        await board.Flow.Cancel(
            Requests.From(board.Owner, new CancelCardRequest(reason), await board.ETagOfAsync(cardId), Guid.NewGuid()), cardId, Ct);
}
