using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>
/// Real-time updates (FR-M06). A browser negotiates a connection, then asks to follow the board it shows;
/// only people who may read a board hear about its changes.
/// </summary>
public sealed class RealtimeEndpoints(IRealtimeGateway realtime, IBoardAuthorizer authorizer)
{
    [Function("NegotiateRealtime")]
    public async Task<IActionResult> Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/realtime/negotiate")] HttpRequest request,
        CancellationToken cancellationToken) =>
        new OkObjectResult(await realtime.NegotiateAsync(request.HttpContext.User.GetObjectId(), cancellationToken));

    [Function("JoinBoard")]
    public async Task<IActionResult> JoinBoard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/realtime/boards/{boardId:guid}/join")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<JoinBoardRequest>(cancellationToken) is not { ConnectionId.Length: > 0 } body)
            return Problems.BodyRequired();
        if (await authorizer.DenyUnlessAsync(request.HttpContext.User, boardId, Permission.BoardRead, cancellationToken) is { } denied)
            return Problems.Forbidden(denied);

        await realtime.JoinBoardAsync(body.ConnectionId, boardId, cancellationToken);
        return new NoContentResult();
    }
}
