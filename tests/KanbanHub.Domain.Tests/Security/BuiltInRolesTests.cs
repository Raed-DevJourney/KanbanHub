using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Tests.Security;

/// <summary>
/// The matrix of specification section 7.2, one row per permission. Each letter is one role, in the order
/// Owner, Flow Manager, Member, Contributor, Viewer, Agent Operator: Y holds the permission, - does not.
/// </summary>
public class BuiltInRolesTests
{
    private static readonly BoardRole[] Columns =
    [
        BuiltInRoles.Owner, BuiltInRoles.FlowManager, BuiltInRoles.Member,
        BuiltInRoles.Contributor, BuiltInRoles.Viewer, BuiltInRoles.AgentOperator
    ];

    [Theory]
    [InlineData(Permission.BoardRead, "YYYYYY")]
    [InlineData(Permission.MetricsView, "YYYYYY")]
    [InlineData(Permission.BoardSettingsEdit, "Y-----")]
    [InlineData(Permission.BoardMembersManage, "Y-----")]
    [InlineData(Permission.BoardDelete, "Y-----")]
    [InlineData(Permission.WorkflowEdit, "YY----")]
    [InlineData(Permission.WipSet, "YY----")]
    [InlineData(Permission.WipOverride, "YY----")]
    [InlineData(Permission.DoneRuleEdit, "YY----")]
    [InlineData(Permission.PaletteEdit, "YY----")]
    [InlineData(Permission.BacklogCreate, "YYYY--")]
    [InlineData(Permission.BacklogOrder, "YYY---")]
    [InlineData(Permission.CardEdit, "YYY---")]            // Contributor: own cards only, see AllowsEditing
    [InlineData(Permission.CardMove, "YYY---")]
    [InlineData(Permission.CardAssign, "YYY---")]
    [InlineData(Permission.CardBlock, "YYY---")]
    [InlineData(Permission.CardMoveBackward, "YY----")]    // Member: configurable, off by default
    [InlineData(Permission.CardCancel, "YY----")]
    [InlineData(Permission.CardDelete, "Y-----")]
    [InlineData(Permission.CommentCreate, "YYYY--")]
    [InlineData(Permission.GitHubLinkConfigure, "Y-----")]
    [InlineData(Permission.GitHubSyncTrigger, "YY----")]
    [InlineData(Permission.AgentDispatch, "Y----Y")]
    [InlineData(Permission.AiUse, "YYYY-Y")]
    [InlineData(Permission.InsightsView, "YYYYYY")]
    [InlineData(Permission.AuditView, "Y-----")]
    [InlineData(Permission.Export, "YY----")]
    public void Each_role_holds_exactly_what_the_matrix_says(Permission permission, string row)
    {
        string actual = string.Concat(Columns.Select(role => role.Permissions.HasFlag(permission) ? 'Y' : '-'));

        Assert.Equal(row, actual);
    }

    [Fact]
    public void A_contributor_may_edit_only_their_own_cards()
    {
        Permission contributor = BuiltInRoles.Contributor.Permissions;

        Assert.True(contributor.AllowsEditing(isOwnCard: true));
        Assert.False(contributor.AllowsEditing(isOwnCard: false));
    }

    [Fact]
    public void A_viewer_may_not_edit_even_a_card_they_created() =>
        Assert.False(BuiltInRoles.Viewer.Permissions.AllowsEditing(isOwnCard: true));

    [Fact]
    public void A_member_may_edit_any_card() =>
        Assert.True(BuiltInRoles.Member.Permissions.AllowsEditing(isOwnCard: false));

    [Fact]
    public void Role_ids_are_unique_and_can_be_looked_up()
    {
        Assert.Equal(BuiltInRoles.All.Count, BuiltInRoles.All.Select(role => role.Id).Distinct().Count());
        Assert.Same(BuiltInRoles.FlowManager, BuiltInRoles.Find(BuiltInRoles.FlowManager.Id));
        Assert.Null(BuiltInRoles.Find(99));
    }

    [Fact]
    public void Permission_names_are_the_ones_the_ui_asks_for()
    {
        Assert.Equal(["BoardRead", "InsightsView", "MetricsView"], BuiltInRoles.Viewer.Permissions.Names());
        Assert.Equal(27, PermissionExtensions.Everything.Names().Count);
    }
}
