using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Planning;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>A board's policies: WIP limits and their calculator, done rules, members.</summary>
public class BoardSettingsTests(KanbanDatabase database)
{
    [Fact]
    public async Task New_limits_show_on_the_board_and_steps_left_out_lose_theirs()
    {
        BoardScenario board = await StartAsync(database);

        IActionResult result = await board.SetLimitsAsync(board.Owner,
            new StepLimit(board.Implement.Id, 4, WipPolicy.AllowWithReason), new StepLimit(board.Validate.Id, 1, WipPolicy.Warn));

        Assert.Equal(204, result.StatusOf());
        BoardSnapshotDto snapshot = await board.SnapshotAsync();
        Assert.Equal([null, 4, 1], snapshot.Steps.Select(step => step.WipLimit));
        Assert.Equal([null, WipPolicy.AllowWithReason, WipPolicy.Warn], snapshot.Steps.Select(step => step.Policy));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task A_limit_outside_1_to_99_is_refused(int maxItems)
    {
        BoardScenario board = await StartAsync(database);

        IActionResult result = await board.SetLimitsAsync(board.Owner, new StepLimit(board.Implement.Id, maxItems, WipPolicy.Strict));

        result.ProblemOf(422, "validation-failed");
    }

    [Fact]
    public async Task A_limit_for_a_step_of_another_board_is_refused()
    {
        BoardScenario board = await StartAsync(database);
        BoardScenario other = await StartAsync(database);

        IActionResult result = await board.SetLimitsAsync(board.Owner, new StepLimit(other.Implement.Id, 3, WipPolicy.Strict));

        result.ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task Every_limit_change_is_audited_with_who_and_why()
    {
        BoardScenario board = await StartAsync(database);

        await board.SetLimitsAsync(board.Owner,
            new StepLimit(board.Specify.Id, 2, WipPolicy.Strict), new StepLimit(board.Implement.Id, 6, WipPolicy.Strict));

        var audit = await database.Connections.QueryAsync(
            "SELECT Field, OldValue, NewValue, Reason, ActorId FROM kb.AuditEvent WHERE BoardId = @BoardId AND EventType = 'wip.limit' ORDER BY Field;",
            new { board.BoardId },
            r => (Field: r.GetString(0), Old: r.GetNullableString(1), New: r.GetNullableString(2), Reason: r.GetString(3), Actor: r.GetGuid(4)));

        Assert.Equal(
            [("Implement", "5 policy 1", "6 policy 1", "Test setup", board.Owner.GetObjectId()),
             ("Validate", "3 policy 1", null, "Test setup", board.Owner.GetObjectId())],
            audit);
    }

    [Fact]
    public async Task The_calculator_reproduces_the_books_example_without_saving_anything()
    {
        BoardScenario board = await StartAsync(database);
        var request = new WipCalculatorRequest(
        [
            new StepCapacityDraft("Specify", ItemsPerPerson: 6, People: 1),
            new StepCapacityDraft("Implement", ItemsPerPerson: 2, People: 3),
            new StepCapacityDraft("Validate", ItemsPerPerson: 3, People: 2)
        ]);

        IActionResult result = await board.Settings.SuggestWipLimits(Requests.From(board.Owner, request), board.BoardId, Ct);

        Assert.Equal([2, 5, 3], result.ValueOf<IReadOnlyList<WipSuggestion>>().Select(s => s.SuggestedLimit));
    }

    [Fact]
    public async Task The_calculator_refuses_a_step_nobody_works_on()
    {
        BoardScenario board = await StartAsync(database);
        var request = new WipCalculatorRequest([new StepCapacityDraft("Specify", ItemsPerPerson: 6, People: 0)]);

        IActionResult result = await board.Settings.SuggestWipLimits(Requests.From(board.Owner, request), board.BoardId, Ct);

        result.ProblemOf(422, "validation-failed");
    }

    [Fact]
    public async Task A_done_rule_shows_on_the_step_and_becomes_the_checklist_of_cards_that_enter()
    {
        BoardScenario board = await StartAsync(database);
        await SetRuleAsync(board, new DoneRuleItemDraft("Reviewed by a teammate", IsRequired: true), new DoneRuleItemDraft("Notes written", IsRequired: false));

        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);

        Assert.Equal(
            [new DoneRuleItemDto("Reviewed by a teammate", true), new DoneRuleItemDto("Notes written", false)],
            (await board.SnapshotAsync()).Steps[0].DoneRule);
        Assert.Equal(["Reviewed by a teammate", "Notes written"], (await board.CardAsync(card)).Checklist.Select(item => item.Text));
    }

    [Fact]
    public async Task A_new_version_of_a_done_rule_replaces_the_old_one()
    {
        BoardScenario board = await StartAsync(database);
        await SetRuleAsync(board, new DoneRuleItemDraft("First version", IsRequired: true));

        await SetRuleAsync(board, new DoneRuleItemDraft("Second version", IsRequired: true));

        Assert.Equal(["Second version"], (await board.SnapshotAsync()).Steps[0].DoneRule.Select(item => item.Text));
    }

    [Fact]
    public async Task A_done_rule_item_needs_text()
    {
        BoardScenario board = await StartAsync(database);
        var request = new SetDoneRuleRequest([new DoneRuleItemDraft("  ", IsRequired: true)]);

        IActionResult result = await board.Settings.SetDoneRule(Requests.From(board.Owner, request), board.BoardId, board.Specify.Id, Ct);

        result.ProblemOf(422, "validation-failed");
    }

    [Fact]
    public async Task New_members_get_in_and_the_board_keeps_an_owner()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal newcomer = await board.UserWithAsync(role: null);
        MemberAssignment owner = new(board.Owner.GetObjectId(), PrincipalType.User, BuiltInRoles.Owner.Id);
        MemberAssignment member = new(newcomer.GetObjectId(), PrincipalType.User, BuiltInRoles.Member.Id);

        IActionResult withoutOwner = await SetMembersAsync(board, member);
        IActionResult withOwner = await SetMembersAsync(board, owner, member);

        withoutOwner.ProblemOf(422, "validation-failed");
        Assert.Equal(204, withOwner.StatusOf());
        Assert.Equal(200, (await board.Boards.GetBoard(Requests.From(newcomer), board.BoardId, Ct)).StatusOf());
    }

    [Fact]
    public async Task The_members_list_shows_who_holds_which_role_on_the_board()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal viewer = await board.UserWithAsync(BuiltInRoles.Viewer);

        var members = (await board.Boards.ListMembers(Requests.From(board.Owner), board.BoardId, Ct)).ValueOf<IReadOnlyList<MemberAssignment>>();

        Assert.Equal(
            [new MemberAssignment(board.Owner.GetObjectId(), PrincipalType.User, BuiltInRoles.Owner.Id),
             new MemberAssignment(viewer.GetObjectId(), PrincipalType.User, BuiltInRoles.Viewer.Id)],
            members);
    }

    [Fact]
    public async Task A_parked_card_tells_the_board_which_step_it_may_return_to()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);

        await board.MoveAsync(board.Owner, card, step: null, Location.Parked);

        CardDto parked = Assert.Single((await board.SnapshotAsync()).Cards);
        Assert.Equal((Location.Parked, board.Specify.Id), (parked.Location, parked.ParkedFromStepId!.Value));
    }

    [Fact]
    public async Task A_member_needs_a_role_that_exists()
    {
        BoardScenario board = await StartAsync(database);

        IActionResult result = await SetMembersAsync(board, new MemberAssignment(Guid.NewGuid(), PrincipalType.User, RoleId: 42));

        result.ProblemOf(422, "validation-failed");
    }

    private static Task<IActionResult> SetMembersAsync(BoardScenario board, params MemberAssignment[] members) =>
        board.Settings.SetMembers(Requests.From(board.Owner, new SetMembersRequest(members)), board.BoardId, Ct);

    private static async Task SetRuleAsync(BoardScenario board, params DoneRuleItemDraft[] items) =>
        Assert.Equal(204, (await board.Settings.SetDoneRule(
            Requests.From(board.Owner, new SetDoneRuleRequest(items)), board.BoardId, board.Specify.Id, Ct)).StatusOf());
}
