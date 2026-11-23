using System.Data;
using System.Security.Claims;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Security;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Boards;

/// <summary>Read side for workspaces and boards. Returns contracts directly: reads carry no domain rules.</summary>
public sealed class BoardQueries(ISqlConnectionFactory database, IPrincipalResolver principals, TimeProvider clock)
{
    // A board is visible to whoever holds a role on it or on its workspace; tenant admins see everything.
    private const string VisibleBoard = """
        (@SeesEverything = 1 OR EXISTS (
            SELECT 1 FROM kb.RoleAssignment ra
            WHERE (ra.BoardId = b.BoardId OR (ra.BoardId IS NULL AND ra.WorkspaceId = b.WorkspaceId))
              AND ra.PrincipalId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@Principals))))
        """;

    private const string BoardsSql = $"""
        SELECT b.BoardId, b.WorkspaceId, b.[Name]
        FROM kb.Board b
        WHERE b.IsArchived = 0 AND {VisibleBoard}
        ORDER BY b.[Name];
        """;

    private const string WorkspacesSql = $"""
        SELECT w.WorkspaceId, w.[Name]
        FROM kb.Workspace w
        WHERE @SeesEverything = 1
           OR EXISTS (SELECT 1 FROM kb.RoleAssignment ra
                      WHERE ra.WorkspaceId = w.WorkspaceId
                        AND ra.PrincipalId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@Principals)))
           OR EXISTS (SELECT 1 FROM kb.Board b WHERE b.WorkspaceId = w.WorkspaceId AND b.IsArchived = 0 AND {VisibleBoard})
        ORDER BY w.[Name];
        """;

    private const string MembersSql = """
        SELECT PrincipalId, PrincipalType, RoleId
        FROM kb.RoleAssignment
        WHERE BoardId = @BoardId
        ORDER BY RoleId, PrincipalId;
        """;

    public async Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await using SqlCommand command = await ForViewerAsync(WorkspacesSql, user, cancellationToken);
        return await database.ListAsync(command, r => new WorkspaceDto(r.GetGuid(0), r.GetString(1)), cancellationToken);
    }

    public async Task<IReadOnlyList<BoardSummaryDto>> ListBoardsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await using SqlCommand command = await ForViewerAsync(BoardsSql, user, cancellationToken);
        return await database.ListAsync(
            command, r => new BoardSummaryDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2)), cancellationToken);
    }

    /// <summary>The board's own role assignments (roles inherited from the workspace are not listed).</summary>
    public async Task<IReadOnlyList<MemberAssignment>> ListMembersAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(MembersSql);
        command.Parameters.AddGuid("@BoardId", boardId);

        return await database.ListAsync(
            command, r => new MemberAssignment(r.GetGuid(0), (PrincipalType)r.GetByte(1), r.GetInt16(2)), cancellationToken);
    }

    /// <summary>The whole board in one round trip, or null when there is no such board.</summary>
    public async Task<BoardSnapshotDto?> FindSnapshotAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await database.OpenAsync(cancellationToken);
        await using SqlCommand command = Sql.Query(BoardSnapshotReader.Sql);
        command.Connection = connection;
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddDateTime2("@NowUtc", clock.GetUtcNow().UtcDateTime);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await BoardSnapshotReader.ReadAsync(reader, cancellationToken);
    }

    private async Task<SqlCommand> ForViewerAsync(string sql, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        SqlCommand command = Sql.Query(sql);
        command.Parameters.AddValue("@SeesEverything", SqlDbType.Bit, user.IsTenantAdmin());
        command.Parameters.AddJson("@Principals", await principals.GetPrincipalIdsAsync(user, cancellationToken));
        return command;
    }
}
