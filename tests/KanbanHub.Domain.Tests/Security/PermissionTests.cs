using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Tests.Security;

public class PermissionTests
{
    /// <summary>Roles persist these bits, so this table is the contract: a failing row means stored roles changed meaning.</summary>
    [Theory]
    [InlineData(Permission.BoardRead, 0)]
    [InlineData(Permission.BoardSettingsEdit, 1)]
    [InlineData(Permission.BoardMembersManage, 2)]
    [InlineData(Permission.BoardDelete, 3)]
    [InlineData(Permission.WorkflowEdit, 4)]
    [InlineData(Permission.WipSet, 5)]
    [InlineData(Permission.WipOverride, 6)]
    [InlineData(Permission.DoneRuleEdit, 7)]
    [InlineData(Permission.PaletteEdit, 8)]
    [InlineData(Permission.BacklogCreate, 9)]
    [InlineData(Permission.BacklogOrder, 10)]
    [InlineData(Permission.CardEdit, 11)]
    [InlineData(Permission.CardMove, 12)]
    [InlineData(Permission.CardMoveBackward, 13)]
    [InlineData(Permission.CardAssign, 14)]
    [InlineData(Permission.CardBlock, 15)]
    [InlineData(Permission.CardCancel, 16)]
    [InlineData(Permission.CardDelete, 17)]
    [InlineData(Permission.CommentCreate, 18)]
    [InlineData(Permission.GitHubLinkConfigure, 19)]
    [InlineData(Permission.GitHubSyncTrigger, 20)]
    [InlineData(Permission.AgentDispatch, 21)]
    [InlineData(Permission.AiUse, 22)]
    [InlineData(Permission.InsightsView, 23)]
    [InlineData(Permission.MetricsView, 24)]
    [InlineData(Permission.AuditView, 25)]
    [InlineData(Permission.Export, 26)]
    public void Each_permission_keeps_its_bit(Permission permission, int bit) =>
        Assert.Equal(1L << bit, (long)permission);

    [Fact]
    public void The_table_above_covers_every_permission() =>
        Assert.Equal(27, Enum.GetValues<Permission>().Count(p => p is not Permission.None));
}
