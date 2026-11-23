using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Data.Boards;
using KanbanHub.Domain;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>Workspaces and boards: list what the caller can see, create new ones, load a board.</summary>
public sealed class BoardEndpoints(BoardCatalog catalog, BoardQueries queries, IBoardAuthorizer authorizer)
{
    [Function("ListWorkspaces")]
    public async Task<IActionResult> ListWorkspaces(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/workspaces")] HttpRequest request,
        CancellationToken cancellationToken) =>
        new OkObjectResult(await queries.ListWorkspacesAsync(request.HttpContext.User, cancellationToken));

    [Function("CreateWorkspace")]
    public async Task<IActionResult> CreateWorkspace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/workspaces")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<CreateWorkspaceRequest>(cancellationToken) is not { } body)
            return Problems.BodyRequired();

        Outcome<Workspace> outcome = await catalog.CreateWorkspaceAsync(body.Name, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(workspace => OutcomeResults.Created(new WorkspaceDto(workspace.Id, workspace.Name)));
    }

    [Function("ListBoards")]
    public async Task<IActionResult> ListBoards(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards")] HttpRequest request,
        CancellationToken cancellationToken) =>
        new OkObjectResult(await queries.ListBoardsAsync(request.HttpContext.User, cancellationToken));

    [Function("CreateBoard")]
    public async Task<IActionResult> CreateBoard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/boards")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<CreateBoardRequest>(cancellationToken) is not { } body)
            return Problems.BodyRequired();

        var command = new CreateBoardCommand(body.WorkspaceId, body.Name, body.TemplateKey, body.TimeZoneId);
        Outcome<NewBoard> outcome = await catalog.CreateBoardAsync(command, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(board => OutcomeResults.Created(new BoardSummaryDto(board.Id, board.WorkspaceId, board.Name)));
    }

    /// <summary>Who holds which role on the board itself, for the members editor.</summary>
    [Function("ListMembers")]
    public async Task<IActionResult> ListMembers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/members")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(request.HttpContext.User, boardId, Permission.BoardMembersManage, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);

        return new OkObjectResult(await queries.ListMembersAsync(boardId, cancellationToken));
    }

    /// <summary>The full snapshot: steps, limits, done rules and cards.</summary>
    [Function("GetBoard")]
    public async Task<IActionResult> GetBoard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(request.HttpContext.User, boardId, Permission.BoardRead, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);

        BoardSnapshotDto? snapshot = await queries.FindSnapshotAsync(boardId, cancellationToken);
        return snapshot is null ? Problems.NotFound(new NotFound("board")) : new OkObjectResult(snapshot);
    }
}
