using static KanbanHub.Domain.Security.Permission;

namespace KanbanHub.Domain.Security;

/// <summary>A named set of board permissions, stored in <c>kb.RoleDefinition</c>.</summary>
public sealed record class BoardRole(short Id, string Name, Permission Permissions);

/// <summary>
/// The six built-in board roles, exactly as the matrix in specification section 7.2.
/// Each role is the one below it plus what its persona adds; Agent Operator is additive on top of Member.
/// </summary>
public static class BuiltInRoles
{
    private const Permission Observe = BoardRead | MetricsView | InsightsView;

    private const Permission Contribute = Observe | BacklogCreate | CommentCreate | AiUse;

    private const Permission Work = Contribute | BacklogOrder | CardEdit | CardMove | CardAssign | CardBlock;

    private const Permission ManageFlow = Work | WorkflowEdit | WipSet | WipOverride | DoneRuleEdit | PaletteEdit
        | CardMoveBackward | CardCancel | GitHubSyncTrigger | Export;

    private const Permission Own = ManageFlow | BoardSettingsEdit | BoardMembersManage | BoardDelete | CardDelete
        | GitHubLinkConfigure | AgentDispatch | AuditView;

    public static BoardRole Owner { get; } = new(1, "Owner", Own);

    public static BoardRole FlowManager { get; } = new(2, "Flow Manager", ManageFlow);

    public static BoardRole Member { get; } = new(3, "Member", Work);

    public static BoardRole Contributor { get; } = new(4, "Contributor", Contribute);

    public static BoardRole Viewer { get; } = new(5, "Viewer", Observe);

    public static BoardRole AgentOperator { get; } = new(6, "Agent Operator", Observe | AgentDispatch | AiUse);

    public static IReadOnlyList<BoardRole> All { get; } = [Owner, FlowManager, Member, Contributor, Viewer, AgentOperator];

    public static BoardRole? Find(short roleId) => All.FirstOrDefault(role => role.Id == roleId);
}
