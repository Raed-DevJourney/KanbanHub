using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>Workspaces and boards: who may create them, what a new board looks like, and who can see it.</summary>
public class BoardLifecycleTests(KanbanDatabase database)
{
    [Fact]
    public async Task Creating_a_workspace_needs_the_board_creator_role()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal plainUser = TestUsers.WithTenantRoles(TenantRoles.User);

        IActionResult result = await board.Boards.CreateWorkspace(Requests.From(plainUser, new CreateWorkspaceRequest("Mine")), Ct);

        Assert.Equal("Missing tenant role: KanbanHub.BoardCreator.", result.ProblemOf(403, "forbidden").Detail);
    }

    [Fact]
    public async Task Creating_a_board_needs_the_board_creator_role()
    {
        BoardScenario board = await StartAsync(database);
        var request = new CreateBoardRequest(board.WorkspaceId, "Another", "quick-start", "UTC");

        IActionResult result = await board.Boards.CreateBoard(Requests.From(TestUsers.WithTenantRoles(), request), Ct);

        result.ProblemOf(403, "forbidden");
    }

    [Fact]
    public async Task A_quick_start_board_has_the_books_steps_limits_and_flags()
    {
        BoardScenario board = await StartAsync(database);

        BoardSnapshotDto snapshot = await board.SnapshotAsync();

        Assert.Equal(["Specify", "Implement", "Validate"], snapshot.Steps.Select(step => step.Name));
        Assert.Equal([1, 2, 3], snapshot.Steps.Select(step => step.Order));
        Assert.Equal([2, 5, 3], snapshot.Steps.Select(step => step.WipLimit));
        Assert.All(snapshot.Steps, step => Assert.Equal(WipPolicy.Strict, step.Policy));
        Assert.Equal([false, false, true], snapshot.Steps.Select(step => step.IsFinal));
        Assert.True(snapshot.Steps[0].CountsParentsOnly);
        Assert.Empty(snapshot.Cards);
    }

    [Fact]
    public async Task An_unknown_template_is_refused()
    {
        BoardScenario board = await StartAsync(database);
        var request = new CreateBoardRequest(board.WorkspaceId, "Odd", "no-such-template", "UTC");

        IActionResult result = await board.Boards.CreateBoard(Requests.From(board.Owner, request), Ct);

        result.ProblemOf(422, "validation-failed");
    }

    [Fact]
    public async Task A_board_cannot_be_created_in_a_workspace_that_does_not_exist()
    {
        BoardScenario board = await StartAsync(database);
        var request = new CreateBoardRequest(Guid.NewGuid(), "Lost", "quick-start", "UTC");

        IActionResult result = await board.Boards.CreateBoard(Requests.From(board.Owner, request), Ct);

        result.ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task The_creator_owns_the_board()
    {
        BoardScenario board = await StartAsync(database);

        IActionResult result = await board.Identity.BoardAccess(Requests.From(board.Owner), board.BoardId, Ct);

        Assert.Equal(BuiltInRoles.Owner.Permissions.Names(), result.ValueOf<BoardAccessResponse>().Permissions);
    }

    [Fact]
    public async Task Someone_without_a_role_cannot_read_the_board_or_find_it_in_their_list()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal stranger = await board.UserWithAsync(role: null);

        (await board.Boards.GetBoard(Requests.From(stranger), board.BoardId, Ct)).ProblemOf(403, "forbidden");
        Assert.DoesNotContain(await ListBoardIdsAsync(board, stranger), id => id == board.BoardId);
        Assert.Empty((await board.Identity.BoardAccess(Requests.From(stranger), board.BoardId, Ct)).ValueOf<BoardAccessResponse>().Permissions);
    }

    [Fact]
    public async Task A_viewer_finds_the_board_and_its_workspace_in_their_lists()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal viewer = await board.UserWithAsync(BuiltInRoles.Viewer);

        var workspaces = (await board.Boards.ListWorkspaces(Requests.From(viewer), Ct)).ValueOf<IReadOnlyList<WorkspaceDto>>();

        Assert.Contains(await ListBoardIdsAsync(board, viewer), id => id == board.BoardId);
        Assert.Equal([board.WorkspaceId], workspaces.Select(workspace => workspace.Id));
    }

    [Fact]
    public async Task A_role_given_to_a_group_reaches_its_members()
    {
        BoardScenario board = await StartAsync(database);
        Guid group = Guid.NewGuid();
        await board.AssignAsync(group, PrincipalType.Group, BuiltInRoles.Member);
        ClaimsPrincipal member = InGroup(group);

        IActionResult result = await board.Identity.BoardAccess(Requests.From(member), board.BoardId, Ct);

        Assert.Equal(BuiltInRoles.Member.Permissions.Names(), result.ValueOf<BoardAccessResponse>().Permissions);
    }

    [Fact]
    public async Task Roles_from_several_assignments_add_up()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal user = await board.UserWithAsync(BuiltInRoles.Member);
        await board.AssignAsync(user.GetObjectId(), PrincipalType.User, BuiltInRoles.AgentOperator);

        var access = (await board.Identity.BoardAccess(Requests.From(user), board.BoardId, Ct)).ValueOf<BoardAccessResponse>();

        Assert.Equal((BuiltInRoles.Member.Permissions | BuiltInRoles.AgentOperator.Permissions).Names(), access.Permissions);
    }

    [Fact]
    public async Task A_workspace_owner_holds_that_role_on_every_board_in_it()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal otherCreator = TestUsers.WithTenantRoles(TenantRoles.BoardCreator);
        var request = new CreateBoardRequest(board.WorkspaceId, "Second board", "quick-start", "UTC");
        Guid second = (await board.Boards.CreateBoard(Requests.From(otherCreator, request), Ct)).ValueOf<BoardSummaryDto>().Id;

        IActionResult result = await board.Identity.BoardAccess(Requests.From(board.Owner), second, Ct);

        Assert.Equal(BuiltInRoles.Owner.Permissions.Names(), result.ValueOf<BoardAccessResponse>().Permissions);
    }

    [Fact]
    public async Task A_tenant_admin_sees_every_board_and_may_do_anything_on_it()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal admin = TestUsers.WithTenantRoles(TenantRoles.Admin);

        var access = (await board.Identity.BoardAccess(Requests.From(admin), board.BoardId, Ct)).ValueOf<BoardAccessResponse>();

        Assert.Equal(27, access.Permissions.Count);
        Assert.Contains(await ListBoardIdsAsync(board, admin), id => id == board.BoardId);
        (await board.Boards.GetBoard(Requests.From(admin), Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task Me_reports_the_caller_and_their_tenant_roles()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal creator = TestUsers.WithTenantRoles(TenantRoles.BoardCreator, TenantRoles.User);

        var me = (await board.Identity.Me(Requests.From(creator), Ct)).ValueOf<MeResponse>();

        Assert.Equal(creator.GetObjectId(), me.UserId);
        Assert.Equal([TenantRoles.BoardCreator, TenantRoles.User], me.TenantRoles);
    }

    private static async Task<IEnumerable<Guid>> ListBoardIdsAsync(BoardScenario board, ClaimsPrincipal user) =>
        (await board.Boards.ListBoards(Requests.From(user), Ct)).ValueOf<IReadOnlyList<BoardSummaryDto>>().Select(b => b.Id);

    private static ClaimsPrincipal InGroup(Guid group)
    {
        Claim[] claims = [new("oid", Guid.NewGuid().ToString()), new("groups", group.ToString())];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", nameType: "name", roleType: "roles"));
    }
}
