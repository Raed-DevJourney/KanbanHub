using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>A board's structure: preferences, deletion, workflow editing and colors.</summary>
public class BoardSetupTests(KanbanDatabase database)
{
    [Fact]
    public async Task A_patch_changes_the_named_preferences_and_the_ready_cap_is_enforced()
    {
        BoardScenario board = await StartAsync(database);
        Guid[] cards = [await board.AddCardAsync(), await board.AddCardAsync()];

        IActionResult result = await board.Setup.Edit(Requests.From(board.Owner, new { name = "Renamed", readyListCap = 1 }), board.BoardId, Ct);

        Assert.Equal(204, result.StatusOf());
        BoardSnapshotDto snapshot = await board.SnapshotAsync();
        Assert.Equal(("Renamed", (short?)1), (snapshot.Name, snapshot.ReadyListCap));
        IActionResult tooLong = await board.Backlog.Reorder(Requests.From(board.Owner, new BacklogOrder(cards, [])), board.BoardId, Ct);
        tooLong.ProblemOf(422, "validation-failed");
    }

    [Theory]
    [InlineData("""{ "timeZoneId": "Mars/Olympus_Mons" }""")]
    [InlineData("""{ "name": "" }""")]
    [InlineData("""{ "archiveAfterDays": 0 }""")]
    [InlineData("""{ "parkingReviewWeeks": "often" }""")]
    public async Task Preferences_that_break_the_rules_are_refused(string patch)
    {
        BoardScenario board = await StartAsync(database);
        object body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(patch);

        IActionResult result = await board.Setup.Edit(Requests.From(board.Owner, body), board.BoardId, Ct);

        result.ProblemOf(422, "validation-failed");
    }

    [Fact]
    public async Task A_deleted_board_is_gone_from_the_api_but_its_history_stays()
    {
        BoardScenario board = await StartAsync(database);
        await board.AddCardAtAsync(board.Specify, Location.Active);

        IActionResult result = await board.Setup.Delete(Requests.From(board.Owner), board.BoardId, Ct);

        Assert.Equal(204, result.StatusOf());
        (await board.Boards.GetBoard(Requests.From(board.Owner), board.BoardId, Ct)).ProblemOf(404, "not-found");
        Assert.DoesNotContain(
            (await board.Boards.ListBoards(Requests.From(board.Owner), Ct)).ValueOf<IReadOnlyList<BoardSummaryDto>>(),
            listed => listed.Id == board.BoardId);
        Assert.Single(await database.Connections.QueryAsync(
            "SELECT MovementId FROM kb.CardMovement WHERE BoardId = @BoardId;", new { board.BoardId }, r => r.GetInt64(0)));
    }

    [Fact]
    public async Task The_workflow_can_gain_rename_and_reorder_steps_and_its_version_goes_up()
    {
        BoardScenario board = await StartAsync(database);
        StepDraft[] steps =
        [
            Keep(board.Specify),
            Keep(board.Implement) with { Name = "Build" },
            new StepDraft(Id: null, "Review", "#C026D3") { HasTrack = true },
            Keep(board.Validate)
        ];

        IActionResult result = await SetWorkflowAsync(board, steps);

        Assert.Equal(204, result.StatusOf());
        BoardSnapshotDto snapshot = await board.SnapshotAsync();
        Assert.Equal(2, snapshot.WorkflowVersion);
        Assert.Equal(["Specify", "Build", "Review", "Validate"], snapshot.Steps.Select(step => step.Name));
        Assert.Equal([false, false, false, true], snapshot.Steps.Select(step => step.IsFinal));
        Assert.Equal([2, 5, null, 3], snapshot.Steps.Select(step => step.WipLimit));
    }

    [Fact]
    public async Task A_card_flows_through_a_step_added_later()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Implement, Location.Done);
        await SetWorkflowAsync(board, Keep(board.Specify), Keep(board.Implement), new StepDraft(null, "Review", "#C026D3"), Keep(board.Validate));
        StepDto review = (await board.SnapshotAsync()).Steps[2];

        IActionResult skipping = await board.MoveAsync(board.Owner, card, board.Validate, Location.Active);
        IActionResult intoReview = await board.MoveAsync(board.Owner, card, review, Location.Active);

        skipping.ProblemOf(422, "illegal-transition");
        Assert.Equal(200, intoReview.StatusOf());
    }

    [Fact]
    public async Task A_step_that_still_holds_cards_cannot_be_removed()
    {
        BoardScenario board = await StartAsync(database);
        await board.AddCardAtAsync(board.Implement, Location.Active);

        IActionResult result = await SetWorkflowAsync(board, Keep(board.Specify), Keep(board.Validate));

        Assert.Contains("Implement still holds cards", result.ProblemOf(422, "validation-failed").Detail);
        Assert.Equal(3, (await board.SnapshotAsync()).Steps.Count);
    }

    [Fact]
    public async Task An_empty_step_can_be_removed_and_is_retired_rather_than_deleted()
    {
        BoardScenario board = await StartAsync(database);

        IActionResult result = await SetWorkflowAsync(board, Keep(board.Specify), Keep(board.Validate));

        Assert.Equal(204, result.StatusOf());
        Assert.Equal(["Specify", "Validate"], (await board.SnapshotAsync()).Steps.Select(step => step.Name));
        Assert.True(Assert.Single(await database.Connections.QueryAsync(
            "SELECT IsRetired FROM kb.BoardStep WHERE StepId = @StepId;", new { StepId = board.Implement.Id }, r => r.GetBoolean(0))));
    }

    [Fact]
    public async Task A_workflow_needs_two_to_twelve_uniquely_named_steps_of_this_board()
    {
        BoardScenario board = await StartAsync(database);
        BoardScenario other = await StartAsync(database);

        (await SetWorkflowAsync(board, Keep(board.Specify))).ProblemOf(422, "validation-failed");
        (await SetWorkflowAsync(board, Keep(board.Specify), Keep(board.Implement) with { Name = "specify" })).ProblemOf(422, "validation-failed");
        (await SetWorkflowAsync(board, Keep(board.Specify), Keep(other.Implement))).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task A_new_board_shows_the_default_palette_and_an_override_changes_one_color()
    {
        BoardScenario board = await StartAsync(database);
        Assert.Equal(Palette.Defaults, (await board.SnapshotAsync()).Palette);

        IActionResult result = await SetPaletteAsync(board, new PaletteColor("status.blocked", "#b91c1c"));

        Assert.Equal(204, result.StatusOf());
        IReadOnlyDictionary<string, string> palette = (await board.SnapshotAsync()).Palette;
        Assert.Equal("#B91C1C", palette["status.blocked"]);
        Assert.Equal(Palette.Defaults["status.done"], palette["status.done"]);
    }

    [Theory]
    [InlineData("status.unknown", "#B91C1C")]
    [InlineData("status.blocked", "red")]
    public async Task A_color_must_be_a_known_token_and_a_hex_value(string key, string color)
    {
        BoardScenario board = await StartAsync(database);

        (await SetPaletteAsync(board, new PaletteColor(key, color))).ProblemOf(422, "validation-failed");
    }

    private static StepDraft Keep(StepDto step) => new(step.Id, step.Name, step.Color)
    {
        HasTrack = step.HasTrack,
        CountsParentsOnly = step.CountsParentsOnly
    };

    private static Task<IActionResult> SetWorkflowAsync(BoardScenario board, params StepDraft[] steps) =>
        board.Setup.SetWorkflow(Requests.From(board.Owner, new SetWorkflowRequest(steps)), board.BoardId, Ct);

    private static Task<IActionResult> SetPaletteAsync(BoardScenario board, params PaletteColor[] colors) =>
        board.Setup.SetPalette(Requests.From(board.Owner, new SetPaletteRequest(colors)), board.BoardId, Ct);
}
