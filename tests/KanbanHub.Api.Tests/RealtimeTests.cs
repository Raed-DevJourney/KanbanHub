using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>Getting a real-time connection, and following a board on it.</summary>
public class RealtimeTests(KanbanDatabase database)
{
    [Fact]
    public async Task Negotiating_gives_the_caller_a_connection_issued_for_them()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal user = TestUsers.WithTenantRoles();

        var connection = (await board.Realtime.Negotiate(Requests.From(user), Ct)).ValueOf<RealtimeConnection>();

        Assert.Equal(new RealtimeConnection(RecordingGateway.Url, user.GetObjectId().ToString()), connection);
    }

    [Fact]
    public async Task Following_a_board_needs_permission_to_read_it()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal viewer = await board.UserWithAsync(BuiltInRoles.Viewer);
        ClaimsPrincipal stranger = await board.UserWithAsync(role: null);

        IActionResult followed = await board.Realtime.JoinBoard(Requests.From(viewer, new JoinBoardRequest("viewer-connection")), board.BoardId, Ct);
        IActionResult refused = await board.Realtime.JoinBoard(Requests.From(stranger, new JoinBoardRequest("stranger-connection")), board.BoardId, Ct);

        Assert.Equal(204, followed.StatusOf());
        refused.ProblemOf(403, "forbidden");
        Assert.Equal([("viewer-connection", board.BoardId)], board.Gateway.Joins);
    }

    [Fact]
    public async Task Following_a_board_needs_a_connection_to_follow_it_with()
    {
        BoardScenario board = await StartAsync(database);

        (await board.Realtime.JoinBoard(Requests.From(board.Owner, new JoinBoardRequest("")), board.BoardId, Ct)).ProblemOf(400, "body-required");
        (await board.Realtime.JoinBoard(Requests.From(board.Owner), board.BoardId, Ct)).ProblemOf(400, "body-required");
        Assert.Empty(board.Gateway.Joins);
    }
}
