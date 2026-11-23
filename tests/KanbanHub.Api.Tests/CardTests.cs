using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>A card's content and flags: create, read, edit, block, checklist, breakdown, backlog order.</summary>
public class CardTests(KanbanDatabase database)
{
    [Fact]
    public async Task A_new_card_lands_in_the_backlog_numbered_and_with_an_etag()
    {
        BoardScenario board = await StartAsync(database);
        HttpRequest request = Requests.From(board.Owner, new CardContent { Title = "Export job", Origin = "F", PriorityBucket = 0 });

        IActionResult result = await board.Backlog.CreateCard(request, board.BoardId, Ct);

        Assert.Equal(201, result.StatusOf());
        Assert.Equal(1, result.ValueOf<CardCreatedDto>().Number);
        Assert.NotEmpty(request.HttpContext.Response.Headers.ETag.ToString());
        CardDto card = Assert.Single((await board.SnapshotAsync()).Cards);
        Assert.Equal(("Export job", Location.Backlog, "F", (byte?)0), (card.Title, card.Location, card.Origin, card.PriorityBucket));
    }

    [Fact]
    public async Task A_card_that_breaks_the_rules_is_refused_with_every_problem()
    {
        BoardScenario board = await StartAsync(database);
        var content = new CardContent { Title = "", Origin = "ZZ" };

        IActionResult result = await board.Backlog.CreateCard(Requests.From(board.Owner, content), board.BoardId, Ct);

        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<string>>(result.ProblemOf(422, "validation-failed").Extensions["errors"]).Count);
    }

    [Fact]
    public async Task Reading_a_card_that_does_not_exist_is_a_404()
    {
        BoardScenario board = await StartAsync(database);

        (await board.Cards.Get(Requests.From(board.Owner), Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task A_patch_changes_only_what_it_names()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync(title: "Draft");
        await PatchAsync(board, board.Owner, card, new { description = "Why we need it", priorityBucket = 2 });

        IActionResult result = await PatchAsync(board, board.Owner, card, new { title = "Final", description = (string?)null });

        Assert.Equal(204, result.StatusOf());
        CardDetailDto edited = await board.CardAsync(card);
        Assert.Equal(("Final", null, (byte?)2), (edited.Card.Title, edited.Description, edited.Card.PriorityBucket));
    }

    [Fact]
    public async Task Each_changed_field_is_audited()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync(title: "Draft");

        await PatchAsync(board, board.Owner, card, new { title = "Final", origin = "U" });

        var audit = await database.Connections.QueryAsync(
            "SELECT Field, OldValue, NewValue FROM kb.AuditEvent WHERE CardId = @CardId AND EventType = 'card.field' ORDER BY Field;",
            new { CardId = card },
            r => (r.GetString(0), r.GetNullableString(1), r.GetNullableString(2)));

        Assert.Equal([("Origin", null, "U"), ("Title", "Draft", "Final")], audit);
    }

    [Fact]
    public async Task An_edit_needs_the_cards_etag_and_a_current_one()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();
        string staleETag = await board.ETagOfAsync(card);
        await PatchAsync(board, board.Owner, card, new { title = "Changed meanwhile" });

        IActionResult withoutETag = await board.Cards.Edit(Requests.From(board.Owner, new { title = "X" }), card, Ct);
        IActionResult withStaleETag = await board.Cards.Edit(Requests.From(board.Owner, new { title = "X" }, staleETag), card, Ct);

        withoutETag.ProblemOf(428, "if-match-required");
        withStaleETag.ProblemOf(412, "precondition-failed");
    }

    [Fact]
    public async Task A_contributor_edits_their_own_card_but_not_someone_elses()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal contributor = await board.UserWithAsync(BuiltInRoles.Contributor);
        Guid own = await board.AddCardAsync(by: contributor);
        Guid someoneElses = await board.AddCardAsync();

        Assert.Equal(204, (await PatchAsync(board, contributor, own, new { title = "Mine, improved" })).StatusOf());
        (await PatchAsync(board, contributor, someoneElses, new { title = "Not mine" })).ProblemOf(403, "forbidden");
    }

    [Fact]
    public async Task Blocking_needs_a_reason_and_shows_on_the_board_until_unblocked()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();

        IActionResult withoutReason = await board.Cards.Block(Requests.From(board.Owner, new Blocker("", BlockerCategory.External)), card, Ct);
        IActionResult blocked = await board.Cards.Block(Requests.From(board.Owner, new Blocker("Waiting for legal", BlockerCategory.External)), card, Ct);

        withoutReason.ProblemOf(422, "validation-failed");
        Assert.Equal(BlockPlacement.Unmoved, blocked.ValueOf<BlockResponse>().Placement);   // a backlog card has no step to be set aside in
        CardDetailDto afterBlock = await board.CardAsync(card);
        Assert.Equal((true, "Waiting for legal", BlockerCategory.External),
            (afterBlock.Card.IsBlocked, afterBlock.Card.BlockedReason, afterBlock.BlockerCategory!.Value));

        await board.Cards.Unblock(Requests.From(board.Owner), card, Ct);
        Assert.False((await board.CardAsync(card)).Card.IsBlocked);
    }

    [Fact]
    public async Task Ticking_a_done_rule_item_toggles_it()
    {
        BoardScenario board = await StartAsync(database);
        var rule = new SetDoneRuleRequest([new("Reviewed", IsRequired: true)]);
        await board.Settings.SetDoneRule(Requests.From(board.Owner, rule), board.BoardId, board.Specify.Id, Ct);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);
        Guid item = (await board.CardAsync(card)).Checklist[0].Id;

        await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, item, Ct);
        Assert.True((await board.CardAsync(card)).Checklist[0].IsChecked);

        await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, item, Ct);
        Assert.False((await board.CardAsync(card)).Checklist[0].IsChecked);

        (await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task A_card_is_broken_down_in_the_breakdown_step_and_still_counts_as_one()
    {
        BoardScenario board = await StartAsync(database);
        Guid inBacklog = await board.AddCardAsync();
        Guid inSpecify = await board.AddCardAtAsync(board.Specify, Location.Active);
        var tasks = new BreakDownRequest(["Design the API", "Write the tests", "Update the docs"]);

        IActionResult tooEarly = await board.Cards.BreakDown(Requests.From(board.Owner, tasks), inBacklog, Ct);
        IActionResult brokenDown = await board.Cards.BreakDown(Requests.From(board.Owner, tasks), inSpecify, Ct);

        tooEarly.ProblemOf(422, "validation-failed");
        Assert.Equal(3, brokenDown.ValueOf<BreakDownResponse>().CardIds.Count);
        BoardSnapshotDto snapshot = await board.SnapshotAsync();
        Assert.Equal(1, snapshot.Steps[0].WipCount);
        Assert.Equal(3, snapshot.Cards.Count(card => card.ParentId == inSpecify && card.CardType == CardType.Task));
        Assert.True(snapshot.Cards.Single(card => card.Id == inSpecify).IsContainer);
    }

    [Fact]
    public async Task The_ready_list_keeps_its_order_and_cards_move_between_buckets()
    {
        BoardScenario board = await StartAsync(database);
        Guid first = await board.AddCardAsync(title: "First");
        Guid second = await board.AddCardAsync(title: "Second");
        Guid third = await board.AddCardAsync(title: "Third");
        var order = new BacklogOrder(Ready: [third, first], Buckets: [new BucketPlacement(second, 3)]);

        IActionResult result = await board.Backlog.Reorder(Requests.From(board.Owner, order), board.BoardId, Ct);

        Assert.Equal(204, result.StatusOf());
        IReadOnlyList<CardDto> cards = (await board.SnapshotAsync()).Cards;
        Assert.Equal([third, first], cards.Where(card => card.IsReady).OrderBy(card => card.Rank).Select(card => card.Id));
        Assert.Equal((byte?)3, cards.Single(card => card.Id == second).PriorityBucket);
    }

    [Fact]
    public async Task A_card_appears_in_the_ready_list_only_once()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();

        IActionResult result = await board.Backlog.Reorder(
            Requests.From(board.Owner, new BacklogOrder(Ready: [card, card], Buckets: [])), board.BoardId, Ct);

        result.ProblemOf(422, "validation-failed");
    }

    private static async Task<IActionResult> PatchAsync(BoardScenario board, ClaimsPrincipal user, Guid cardId, object patch) =>
        await board.Cards.Edit(Requests.From(user, patch, await board.ETagOfAsync(cardId)), cardId, Ct);
}
