using KanbanHub.Domain.Boards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Boards;

/// <summary>Stores workspaces, boards and their settings through the write procedures.</summary>
public sealed class BoardStore(ISqlConnectionFactory database) : IBoardStore
{
    public async Task CreateWorkspaceAsync(Workspace workspace, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_CreateWorkspace");
        command.Parameters.AddGuid("@WorkspaceId", workspace.Id);
        command.Parameters.AddText("@Name", workspace.Name, size: 200);
        command.Parameters.AddStamp(stamp);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<bool> CreateBoardAsync(NewBoard board, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_CreateBoard");
        command.Parameters.AddGuid("@BoardId", board.Id);
        command.Parameters.AddGuid("@WorkspaceId", board.WorkspaceId);
        command.Parameters.AddText("@Name", board.Name, size: 200);
        command.Parameters.AddValue("@TemplateKey", System.Data.SqlDbType.VarChar, board.Template.Key, size: 40);
        command.Parameters.AddValue("@TimeZoneId", System.Data.SqlDbType.VarChar, board.TimeZoneId, size: 64);
        command.Parameters.AddJson("@StepsJson", board.Template.Steps);
        command.Parameters.AddStamp(new Stamp(board.OwnerId, board.Now));

        return await database.ResultCodeAsync(command, cancellationToken) == ResultCodes.Ok;
    }

    public async Task<bool> SetWipLimitsAsync(WipLimitChange change, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetWipLimits");
        command.Parameters.AddGuid("@BoardId", change.BoardId);
        command.Parameters.AddJson("@LimitsJson", change.Limits);
        command.Parameters.AddText("@Reason", change.Reason, size: 300);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) == ResultCodes.Ok;
    }

    public async Task<bool> SetDoneRuleAsync(DoneRuleChange change, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetDoneRule");
        command.Parameters.AddGuid("@BoardId", change.BoardId);
        command.Parameters.AddGuid("@StepId", change.StepId);
        command.Parameters.AddTinyInt("@CardType", change.CardType);
        command.Parameters.AddJson("@ItemsJson", change.Items);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) == ResultCodes.Ok;
    }

    public async Task SetMembersAsync(
        Guid boardId, IReadOnlyList<MemberAssignment> members, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetBoardMembers");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddJson("@MembersJson", members);
        command.Parameters.AddStamp(stamp);

        await database.ExecuteAsync(command, cancellationToken);
    }
}
