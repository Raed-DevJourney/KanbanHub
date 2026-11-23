using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>
/// Every permission-guarded endpoint, called by every built-in role and by someone with no role:
/// the API answers 403 exactly when the role lacks the endpoint's permission (specification sections 7.2 and 19.1).
/// </summary>
public class RoleEndpointMatrixTests(KanbanDatabase database)
{
    private const string NoRole = "No role";

    /// <summary>What an endpoint needs, and how to call it on a board that has one card in Specify Active.</summary>
    private sealed record Endpoint(Permission Required, Func<BoardScenario, ClaimsPrincipal, Guid, Task<IActionResult>> Call);

    private static readonly Dictionary<string, Endpoint> Endpoints = new()
    {
        ["PATCH board"] = new(Permission.BoardSettingsEdit, (board, user, _) =>
            board.Setup.Edit(Requests.From(user, new { name = "Renamed" }), board.BoardId, Ct)),

        ["DELETE board"] = new(Permission.BoardDelete, (board, user, _) =>
            board.Setup.Delete(Requests.From(user), board.BoardId, Ct)),

        ["PUT workflow"] = new(Permission.WorkflowEdit, (board, user, _) =>
            board.Setup.SetWorkflow(Requests.From(user, new SetWorkflowRequest([])), board.BoardId, Ct)),

        ["PUT palette"] = new(Permission.PaletteEdit, (board, user, _) =>
            board.Setup.SetPalette(Requests.From(user, new SetPaletteRequest([])), board.BoardId, Ct)),

        ["GET audit"] = new(Permission.AuditView, (board, user, _) =>
            board.Insights.Audit(Requests.From(user), board.BoardId, Ct)),

        ["GET metrics"] = new(Permission.MetricsView, (board, user, _) =>
            board.Insights.Metrics(Requests.From(user), board.BoardId, "forecast", Ct)),

        ["GET comments"] = new(Permission.BoardRead, (board, user, card) =>
            board.Comments.List(Requests.From(user), card, Ct)),

        ["POST comments"] = new(Permission.CommentCreate, (board, user, card) =>
            board.Comments.Add(Requests.From(user, new AddCommentRequest("A thought")), card, Ct)),
        ["GET board"] = new(Permission.BoardRead, (board, user, _) =>
            board.Boards.GetBoard(Requests.From(user), board.BoardId, Ct)),

        ["GET card"] = new(Permission.BoardRead, (board, user, card) =>
            board.Cards.Get(Requests.From(user), card, Ct)),

        ["PUT wip-limits"] = new(Permission.WipSet, (board, user, _) =>
            board.SetLimitsAsync(user, new StepLimit(board.Implement.Id, 4, WipPolicy.Strict))),

        ["POST wip-calculator"] = new(Permission.WipSet, (board, user, _) =>
            board.Settings.SuggestWipLimits(
                Requests.From(user, new WipCalculatorRequest([new StepCapacityDraft("Implement", 2, 3)])), board.BoardId, Ct)),

        ["PUT done-rule"] = new(Permission.DoneRuleEdit, (board, user, _) =>
            board.Settings.SetDoneRule(
                Requests.From(user, new SetDoneRuleRequest([new DoneRuleItemDraft("Reviewed", true)])), board.BoardId, board.Implement.Id, Ct)),

        ["GET members"] = new(Permission.BoardMembersManage, (board, user, _) =>
            board.Boards.ListMembers(Requests.From(user), board.BoardId, Ct)),

        ["PUT members"] = new(Permission.BoardMembersManage, (board, user, _) =>
            board.Settings.SetMembers(
                Requests.From(user, new SetMembersRequest([new MemberAssignment(user.GetObjectId(), PrincipalType.User, BuiltInRoles.Owner.Id)])),
                board.BoardId, Ct)),

        ["POST cards"] = new(Permission.BacklogCreate, (board, user, _) =>
            board.Backlog.CreateCard(Requests.From(user, new CardContent { Title = "New" }), board.BoardId, Ct)),

        ["PUT backlog order"] = new(Permission.BacklogOrder, (board, user, _) =>
            board.Backlog.Reorder(Requests.From(user, new BacklogOrder([], [])), board.BoardId, Ct)),

        ["PATCH card"] = new(Permission.CardEdit, async (board, user, card) =>
            await board.Cards.Edit(Requests.From(user, new { title = "Edited" }, await board.ETagOfAsync(card)), card, Ct)),

        ["POST block"] = new(Permission.CardBlock, (board, user, card) =>
            board.Cards.Block(Requests.From(user, new Blocker("Waiting", BlockerCategory.External)), card, Ct)),

        ["POST unblock"] = new(Permission.CardBlock, (board, user, card) =>
            board.Cards.Unblock(Requests.From(user), card, Ct)),

        ["POST checklist toggle"] = new(Permission.CardEdit, (board, user, card) =>
            board.Cards.ToggleChecklistItem(Requests.From(user), card, Guid.NewGuid(), Ct)),

        ["POST breakdown"] = new(Permission.CardEdit, (board, user, card) =>
            board.Cards.BreakDown(Requests.From(user, new BreakDownRequest(["A task"])), card, Ct)),

        ["POST move"] = new(Permission.CardMove, (board, user, card) =>
            board.MoveAsync(user, card, board.Specify, Location.Done)),

        ["POST move backward"] = new(Permission.CardMove | Permission.CardMoveBackward, async (board, user, card) =>
        {
            await board.MustMoveAsync(card, board.Specify, Location.Done);
            return await board.MoveAsync(user, card, board.Specify, Location.Active, reason: "Not done after all");
        }),

        ["POST cancel"] = new(Permission.CardCancel, async (board, user, card) =>
            await board.Flow.Cancel(
                Requests.From(user, new CancelCardRequest("No longer needed"), await board.ETagOfAsync(card), Guid.NewGuid()), card, Ct)),

        ["POST realtime join"] = new(Permission.BoardRead, (board, user, _) =>
            board.Realtime.JoinBoard(Requests.From(user, new JoinBoardRequest("connection-1")), board.BoardId, Ct)),

        ["PUT card mute"] = new(Permission.BoardRead, (board, user, card) =>
            board.Inbox.Mute(Requests.From(user), card, Ct)),

        ["GET github links"] = new(Permission.BoardRead, (board, user, _) =>
            board.GitHubLinks.ListRepositories(Requests.From(user), board.BoardId, Ct)),

        ["POST github links"] = new(Permission.GitHubLinkConfigure, (board, user, _) =>
            board.GitHubLinks.LinkRepository(
                Requests.From(user, new LinkRepositoryRequest("contoso/checkout", KanbanHub.Domain.GitHub.SyncMode.OnDemand)), board.BoardId, Ct)),

        ["DELETE github links"] = new(Permission.GitHubLinkConfigure, (board, user, _) =>
            board.GitHubLinks.UnlinkRepository(Requests.From(user), board.BoardId, repositoryId: 1, Ct)),

        ["POST github link"] = new(Permission.CardEdit, (board, user, card) =>
            board.GitHubLinks.LinkIssue(Requests.From(user, new LinkIssueRequest(RepositoryId: 1)), card, Ct)),

        ["DELETE github link"] = new(Permission.CardEdit, (board, user, card) =>
            board.GitHubLinks.UnlinkIssue(Requests.From(user), card, Ct)),

        ["POST github sync"] = new(Permission.GitHubSyncTrigger, (board, user, card) =>
            board.GitHubLinks.Sync(Requests.From(user), card, Ct)),

        ["GET agent dispatch"] = new(Permission.BoardRead, (board, user, card) =>
            board.Agents.Describe(Requests.From(user), card, Ct)),

        ["POST agent dispatch"] = new(Permission.AgentDispatch, (board, user, card) =>
            board.Agents.Dispatch(Requests.From(user, new DispatchAgentRequest("copilot", "main")), card, Ct)),

        ["DELETE agent dispatch"] = new(Permission.AgentDispatch, (board, user, card) =>
            board.Agents.End(Requests.From(user), card, Ct)),

        ["POST ai breakdown"] = new(Permission.AiUse, (board, user, card) =>
            board.CardAi.Breakdown(Requests.From(user), card, Ct)),

        ["POST ai triage"] = new(Permission.AiUse, (board, user, card) =>
            board.CardAi.Triage(Requests.From(user), card, Ct)),

        ["POST ai done-rule check"] = new(Permission.AiUse, (board, user, card) =>
            board.CardAi.DoneRuleCheck(Requests.From(user), card, Ct)),

        ["POST ai agent instructions"] = new(Permission.AiUse, (board, user, card) =>
            board.CardAi.AgentInstructions(Requests.From(user), card, Ct)),

        ["POST ai duplicate dismiss"] = new(Permission.CardEdit, (board, user, card) =>
            board.CardAi.DismissDuplicate(Requests.From(user), card, Guid.NewGuid(), Ct)),

        ["POST ai forecast narrative"] = new(Permission.AiUse | Permission.MetricsView, (board, user, _) =>
            board.BoardAi.ForecastNarrative(Requests.From(user), board.BoardId, Ct)),

        ["GET ai status"] = new(Permission.BoardRead, (board, user, _) =>
            board.BoardAi.Status(Requests.From(user), board.BoardId, Ct)),

        ["GET insights"] = new(Permission.InsightsView, (board, user, _) =>
            board.BoardAi.List(Requests.From(user), board.BoardId, Ct)),

        ["POST insight dismiss"] = new(Permission.WipSet, (board, user, _) =>
            board.BoardAi.Dismiss(Requests.From(user), board.BoardId, insightId: 1, Ct)),

        ["POST insight accept"] = new(Permission.WipSet, (board, user, _) =>
            board.BoardAi.Accept(Requests.From(user), board.BoardId, insightId: 1, Ct))
    };

    public static TheoryData<string, string> EveryEndpointForEveryRole()
    {
        var cases = new TheoryData<string, string>();
        foreach (string endpoint in Endpoints.Keys)
            foreach (string role in BuiltInRoles.All.Select(role => role.Name).Append(NoRole))
                cases.Add(endpoint, role);
        return cases;
    }

    [Theory]
    [MemberData(nameof(EveryEndpointForEveryRole))]
    public async Task An_endpoint_answers_403_exactly_when_the_role_lacks_its_permission(string endpointName, string roleName)
    {
        Endpoint endpoint = Endpoints[endpointName];
        BoardRole? role = BuiltInRoles.All.SingleOrDefault(candidate => candidate.Name == roleName);
        BoardScenario board = await StartAsync(database);
        Guid ownersCard = await board.AddCardAtAsync(board.Specify, Location.Active);
        ClaimsPrincipal user = await board.UserWithAsync(role);

        IActionResult result = await endpoint.Call(board, user, ownersCard);

        bool lacksPermission = role is null || !role.Permissions.HasFlag(endpoint.Required);
        Assert.Equal(lacksPermission, result.StatusOf() == 403);
    }

    [Fact]
    public void The_matrix_covers_every_permission_this_phase_enforces()
    {
        Permission covered = Endpoints.Values.Aggregate(Permission.None, (all, endpoint) => all | endpoint.Required);

        Assert.Equal(
            Permission.BoardRead | Permission.BoardSettingsEdit | Permission.BoardMembersManage | Permission.BoardDelete
            | Permission.WorkflowEdit | Permission.WipSet | Permission.DoneRuleEdit | Permission.PaletteEdit
            | Permission.BacklogCreate | Permission.BacklogOrder | Permission.CardEdit | Permission.CardBlock
            | Permission.CardMove | Permission.CardMoveBackward | Permission.CardCancel | Permission.CommentCreate
            | Permission.MetricsView | Permission.AuditView | Permission.GitHubLinkConfigure | Permission.GitHubSyncTrigger
            | Permission.AgentDispatch | Permission.AiUse | Permission.InsightsView,
            covered);
    }
}
