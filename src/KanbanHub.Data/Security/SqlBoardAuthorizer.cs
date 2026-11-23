using System.Security.Claims;
using KanbanHub.Domain.Security;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Security;

/// <summary>
/// Effective board permissions: the union of every role assigned, on the board or on its workspace,
/// to the caller or to one of their groups (specification section 7.2). A tenant admin holds everything.
/// </summary>
public sealed class SqlBoardAuthorizer(ISqlConnectionFactory database, IPrincipalResolver principals) : IBoardAuthorizer
{
    private const string Sql = """
        SELECT r.Permissions
        FROM kb.RoleAssignment ra
        JOIN kb.RoleDefinition r ON r.RoleId = ra.RoleId
        JOIN kb.Board b ON b.BoardId = @BoardId
        WHERE (ra.BoardId = @BoardId OR (ra.BoardId IS NULL AND ra.WorkspaceId = b.WorkspaceId))
          AND ra.PrincipalId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@Principals));
        """;

    public async Task<Permission> GetEffectiveAsync(ClaimsPrincipal user, Guid boardId, CancellationToken cancellationToken)
    {
        if (user.IsTenantAdmin()) return PermissionExtensions.Everything;

        await using SqlCommand command = Data.Sql.Query(Sql);
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddJson("@Principals", await principals.GetPrincipalIdsAsync(user, cancellationToken));

        List<Permission> granted = await database.ListAsync(command, r => (Permission)r.GetInt64(0), cancellationToken);
        return granted.Aggregate(Permission.None, (all, role) => all | role);
    }
}
