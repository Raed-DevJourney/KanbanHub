namespace KanbanHub.Domain.Boards;

/// <summary>Storage port for workspaces, boards and their settings.</summary>
public interface IBoardStore
{
    /// <summary>Stores the workspace and makes its creator the workspace Owner.</summary>
    Task CreateWorkspaceAsync(Workspace workspace, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Stores the board with its steps and limits. Returns false when the workspace doesn't exist.</summary>
    Task<bool> CreateBoardAsync(NewBoard board, CancellationToken cancellationToken);

    /// <summary>Replaces the limits of the named steps. Returns false when a step isn't an open step of the board.</summary>
    Task<bool> SetWipLimitsAsync(WipLimitChange change, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Stores a new version of the done rule. Returns false when the step isn't an open step of the board.</summary>
    Task<bool> SetDoneRuleAsync(DoneRuleChange change, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Replaces the board's own role assignments (workspace-level ones are untouched).</summary>
    Task SetMembersAsync(Guid boardId, IReadOnlyList<MemberAssignment> members, Stamp stamp, CancellationToken cancellationToken);
}
