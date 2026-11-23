using System.Security.Claims;
using KanbanHub.Contracts;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>Who the caller is, and what they may do on a board. Health is the one anonymous endpoint.</summary>
public sealed class IdentityEndpoints(IBoardAuthorizer authorizer, Inbox inbox)
{
    private static readonly string[] KnownTenantRoles =
        [TenantRoles.Admin, TenantRoles.BoardCreator, TenantRoles.Auditor, TenantRoles.User, TenantRoles.Integration];

    [Function("Health")]
    public static IActionResult Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest request) =>
        new OkObjectResult("Healthy");

    /// <summary>The web app calls this once at start, which is also when the caller's name and email are remembered.</summary>
    [Function("GetMe")]
    public async Task<IActionResult> Me(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/me")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ClaimsPrincipal user = request.HttpContext.User;
        await inbox.RememberAsync(user, cancellationToken);
        return new OkObjectResult(new MeResponse(
            user.GetObjectId(), user.Identity?.Name, [.. KnownTenantRoles.Where(user.IsInRole)]));
    }

    /// <summary>The permission names the UI uses to decide which actions to render (specification section 16.1).</summary>
    [Function("GetBoardAccess")]
    public async Task<IActionResult> BoardAccess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/boards/{boardId:guid}/me")] HttpRequest request,
        Guid boardId, CancellationToken cancellationToken)
    {
        Permission granted = await authorizer.GetEffectiveAsync(request.HttpContext.User, boardId, cancellationToken);
        return new OkObjectResult(new BoardAccessResponse(granted.Names()));
    }
}
