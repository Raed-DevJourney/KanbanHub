using System.Diagnostics.CodeAnalysis;

namespace KanbanHub.Domain.Security;

/// <summary>
/// Board permissions (specification section 7.2). Roles store them as a <c>bigint</c> in
/// <c>kb.RoleDefinition.Permissions</c>, so a bit, once assigned, must never change meaning.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The specification names this type; it has nothing to do with code access security.")]
public enum Permission : long
{
    None = 0,
    BoardRead = 1L << 0,
    BoardSettingsEdit = 1L << 1,
    BoardMembersManage = 1L << 2,
    BoardDelete = 1L << 3,
    WorkflowEdit = 1L << 4,
    WipSet = 1L << 5,
    WipOverride = 1L << 6,
    DoneRuleEdit = 1L << 7,
    PaletteEdit = 1L << 8,
    BacklogCreate = 1L << 9,
    BacklogOrder = 1L << 10,
    CardEdit = 1L << 11,
    CardMove = 1L << 12,
    CardMoveBackward = 1L << 13,
    CardAssign = 1L << 14,
    CardBlock = 1L << 15,
    CardCancel = 1L << 16,
    CardDelete = 1L << 17,
    CommentCreate = 1L << 18,
    GitHubLinkConfigure = 1L << 19,
    GitHubSyncTrigger = 1L << 20,
    AgentDispatch = 1L << 21,
    AiUse = 1L << 22,
    InsightsView = 1L << 23,
    MetricsView = 1L << 24,
    AuditView = 1L << 25,
    Export = 1L << 26
}
