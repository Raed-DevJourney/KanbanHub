using System.Security.Claims;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Security;

/// <summary>The tenant-wide Entra app roles of specification section 7.1.</summary>
public static class TenantRoles
{
    public const string Admin = "KanbanHub.Admin";
    public const string BoardCreator = "KanbanHub.BoardCreator";
    public const string Auditor = "KanbanHub.Auditor";
    public const string User = "KanbanHub.User";
    public const string Integration = "KanbanHub.Integration";
}

/// <summary>Resolves who a caller is for role assignments: the user and every group they belong to.</summary>
public interface IPrincipalResolver
{
    /// <summary>The caller's Entra object id followed by the ids of their groups, transitively.</summary>
    Task<IReadOnlyList<Guid>> GetPrincipalIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

public static class PermissionExtensions
{
    /// <summary>What a tenant administrator holds: every permission, including ones added later.</summary>
    public const Permission Everything = (Permission)long.MaxValue;

    /// <summary>The names of the individual permissions in a set, as <c>GET /boards/{id}/me</c> returns them.</summary>
    public static IReadOnlyList<string> Names(this Permission granted) =>
        [.. Enum.GetValues<Permission>().Where(p => p != Permission.None && granted.HasFlag(p)).Select(p => p.ToString())];

    /// <summary>
    /// The matrix's "own cards" rule: whoever may add backlog items may edit the cards they created,
    /// even without <see cref="Permission.CardEdit"/>.
    /// </summary>
    public static bool AllowsEditing(this Permission granted, bool isOwnCard) =>
        granted.HasFlag(Permission.CardEdit) || (isOwnCard && granted.HasFlag(Permission.BacklogCreate));
}

public static class AccessExtensions
{
    public static bool IsTenantAdmin(this ClaimsPrincipal user) => user.IsInRole(TenantRoles.Admin);

    public static bool MayCreateBoards(this ClaimsPrincipal user) =>
        user.IsTenantAdmin() || user.IsInRole(TenantRoles.BoardCreator);

    /// <summary>Returns the refusal when the caller lacks <paramref name="required"/> on the board, or null when they hold it.</summary>
    public static async Task<Forbidden?> DenyUnlessAsync(
        this IBoardAuthorizer authorizer, ClaimsPrincipal user, Guid boardId, Permission required,
        CancellationToken cancellationToken)
    {
        Permission granted = await authorizer.GetEffectiveAsync(user, boardId, cancellationToken);
        return granted.HasFlag(required) ? null : new Forbidden(required);
    }
}
