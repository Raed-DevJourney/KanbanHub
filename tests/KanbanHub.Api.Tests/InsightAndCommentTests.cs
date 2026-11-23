using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>What a board's history tells (audit, forecast) and the conversation on its cards.</summary>
public class InsightAndCommentTests(KanbanDatabase database)
{
    [Fact]
    public async Task The_audit_trail_lists_moves_and_changes_newest_first_one_page_at_a_time()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);
        await board.Cards.Block(Requests.From(board.Owner, new Blocker("Waiting for legal", BlockerCategory.External)), card, Ct);

        var all = await AuditAsync(board, board.Owner, "?take=10");
        var firstPage = await AuditAsync(board, board.Owner, "?take=1");
        var secondPage = await AuditAsync(board, board.Owner, $"?take=10&before={Uri.EscapeDataString(firstPage[0].OccurredAtUtc.ToString("O"))}");

        // Blocking recorded the flag and, in the same instant, the move to the Blocked sub-column it caused.
        Assert.Equal(["card.moved", "card.blocked", "card.moved", "board.created"], all.Select(entry => entry.EventType));
        Assert.Equal(("1 Specify", "3 Specify", "Blocked"), (all[0].OldValue, all[0].NewValue, all[0].Reason));
        Assert.Equal("Waiting for legal", all[1].Reason);
        Assert.Equal(("0 ", "1 Specify", board.Owner.GetObjectId()), (all[2].OldValue, all[2].NewValue, all[2].ActorId));

        // A page never splits entries of one instant, so paging by time loses nothing.
        Assert.Equal(["card.moved", "card.blocked"], firstPage.Select(entry => entry.EventType));
        Assert.Equal(["card.moved", "board.created"], secondPage.Select(entry => entry.EventType));
    }

    [Fact]
    public async Task A_tenant_auditor_reads_the_trail_of_a_board_they_hold_no_role_on()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal auditor = TestUsers.WithTenantRoles(TenantRoles.Auditor);

        Assert.NotEmpty(await AuditAsync(board, auditor, "?take=5"));
    }

    [Fact]
    public async Task The_forecast_is_the_books_formula_over_the_mvp_bucket()
    {
        BoardScenario board = await StartAsync(database);
        await board.AddCardAsync(new CardContent { Title = "Big item", PriorityBucket = 0, EstimatedTasks = 3 });
        await board.AddCardAsync(new CardContent { Title = "Later", PriorityBucket = 2 });
        Guid inProgress = await board.AddCardAsync(new CardContent { Title = "Started", PriorityBucket = 0 });
        Guid finished = await board.AddCardAsync(new CardContent { Title = "Shipped", PriorityBucket = 0 });
        await board.WalkToAsync(inProgress, board.Specify, Location.Active);
        await board.WalkToAsync(finished, board.Validate, Location.Done);
        await BackdateCardsAsync(board, days: 30);

        var forecast = (await board.Insights.Metrics(Requests.From(board.Owner), board.BoardId, "forecast", Ct)).ValueOf<ForecastDto>();

        // 14-day window: nothing was added, one task finished, four remain (3 estimated + 1 in progress).
        Assert.Equal((14, (byte?)0, 4, false), (forecast.WindowDays, forecast.PriorityBucket, forecast.CurrentTaskEstimate, forecast.NeverFinishes));
        Assert.Equal(1 / 14.0, forecast.CompletionPerDay, precision: 10);
        Assert.Equal(0, forecast.AddPerDay, precision: 10);
        Assert.Equal(56, forecast.DaysRemaining!.Value, precision: 6);
    }

    [Fact]
    public async Task Work_added_faster_than_it_finishes_raises_the_never_finishes_warning()
    {
        BoardScenario board = await StartAsync(database);
        await board.AddCardAsync(new CardContent { Title = "Just added", PriorityBucket = 1, EstimatedTasks = 5 });

        var forecast = (await board.Insights.Metrics(
            Requests.WithQuery(board.Owner, "?bucket=all&days=7"), board.BoardId, "forecast", Ct)).ValueOf<ForecastDto>();

        Assert.Equal((7, null, 5, true, null),
            (forecast.WindowDays, forecast.PriorityBucket, forecast.CurrentTaskEstimate, forecast.NeverFinishes, forecast.DaysRemaining));
    }

    [Fact]
    public async Task A_metric_that_does_not_exist_yet_is_a_404()
    {
        BoardScenario board = await StartAsync(database);

        (await board.Insights.Metrics(Requests.From(board.Owner), board.BoardId, "cfd", Ct)).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task Comments_are_kept_in_the_order_they_were_written()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal contributor = await board.UserWithAsync(BuiltInRoles.Contributor);
        Guid card = await board.AddCardAsync();

        await board.Comments.Add(Requests.From(board.Owner, new AddCommentRequest("Is this **still** needed?")), card, Ct);
        IActionResult reply = await board.Comments.Add(Requests.From(contributor, new AddCommentRequest("  Yes, for the audit.  ")), card, Ct);

        Assert.Equal(201, reply.StatusOf());
        var comments = (await board.Comments.List(Requests.From(contributor), card, Ct)).ValueOf<IReadOnlyList<CommentDto>>();
        Assert.Equal(["Is this **still** needed?", "Yes, for the audit."], comments.Select(comment => comment.Body));
        Assert.Equal(contributor.GetObjectId(), comments[1].AuthorId);
    }

    [Fact]
    public async Task A_comment_needs_text_and_a_card()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAsync();

        (await board.Comments.Add(Requests.From(board.Owner, new AddCommentRequest("   ")), card, Ct)).ProblemOf(422, "validation-failed");
        (await board.Comments.Add(Requests.From(board.Owner, new AddCommentRequest("Hello")), Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
        (await board.Comments.List(Requests.From(board.Owner), Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
    }

    private static async Task<IReadOnlyList<AuditEntryDto>> AuditAsync(BoardScenario board, ClaimsPrincipal user, string query) =>
        (await board.Insights.Audit(Requests.WithQuery(user, query), board.BoardId, Ct)).ValueOf<IReadOnlyList<AuditEntryDto>>();

    /// <summary>Makes the board's cards look as if they were all added before the forecast window opened.</summary>
    private Task BackdateCardsAsync(BoardScenario board, int days) =>
        database.Connections.ExecuteAsync(
            "UPDATE kb.Card SET CreatedAtUtc = DATEADD(DAY, -@Days, SYSUTCDATETIME()) WHERE BoardId = @BoardId;",
            new { board.BoardId, Days = days });
}
