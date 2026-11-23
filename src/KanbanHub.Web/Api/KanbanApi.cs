using System.Text.Json.Nodes;
using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Planning;

namespace KanbanHub.Web.Api;

/// <summary>The KanbanHub API, one method per endpoint the web app uses.</summary>
public sealed class KanbanApi(ApiTransport transport)
{
    private static CardPreconditions None => new();

    // ---- identity and boards

    public Task<ApiResult<MeResponse>> GetMeAsync(CancellationToken ct) =>
        transport.GetAsync<MeResponse>("me", ct);

    public Task<ApiResult<BoardAccessResponse>> GetBoardAccessAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<BoardAccessResponse>($"boards/{boardId}/me", ct);

    public Task<ApiResult<IReadOnlyList<WorkspaceDto>>> ListWorkspacesAsync(CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<WorkspaceDto>>("workspaces", ct);

    public Task<ApiResult<WorkspaceDto>> CreateWorkspaceAsync(string name, CancellationToken ct) =>
        transport.SendAsync<CreateWorkspaceRequest, WorkspaceDto>(HttpMethod.Post, "workspaces", new(name), None, ct);

    public Task<ApiResult<IReadOnlyList<BoardSummaryDto>>> ListBoardsAsync(CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<BoardSummaryDto>>("boards", ct);

    public Task<ApiResult<BoardSummaryDto>> CreateBoardAsync(CreateBoardRequest request, CancellationToken ct) =>
        transport.SendAsync<CreateBoardRequest, BoardSummaryDto>(HttpMethod.Post, "boards", request, None, ct);

    public Task<ApiResult<BoardSnapshotDto>> GetBoardAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<BoardSnapshotDto>($"boards/{boardId}", ct);

    // ---- board settings

    public Task<ApiResult<Done>> EditBoardAsync(Guid boardId, JsonObject patch, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Patch, $"boards/{boardId}", patch, None, ct);

    public Task<ApiResult<Done>> DeleteBoardAsync(Guid boardId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"boards/{boardId}", ct);

    public Task<ApiResult<Done>> SetWorkflowAsync(Guid boardId, IReadOnlyList<StepDraft> steps, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"boards/{boardId}/workflow", new SetWorkflowRequest(steps), None, ct);

    public Task<ApiResult<Done>> SetWipLimitsAsync(Guid boardId, SetWipLimitsRequest request, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"boards/{boardId}/wip-limits", request, None, ct);

    public Task<ApiResult<IReadOnlyList<WipSuggestion>>> SuggestWipLimitsAsync(
        Guid boardId, IReadOnlyList<StepCapacityDraft> steps, CancellationToken ct) =>
        transport.SendAsync<WipCalculatorRequest, IReadOnlyList<WipSuggestion>>(
            HttpMethod.Post, $"boards/{boardId}/wip-calculator", new(steps), None, ct);

    public Task<ApiResult<Done>> SetDoneRuleAsync(Guid boardId, Guid stepId, IReadOnlyList<DoneRuleItemDraft> items, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"boards/{boardId}/steps/{stepId}/done-rule", new SetDoneRuleRequest(items), None, ct);

    public Task<ApiResult<Done>> SetPaletteAsync(Guid boardId, IReadOnlyList<PaletteColor> colors, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"boards/{boardId}/palette", new SetPaletteRequest(colors), None, ct);

    public Task<ApiResult<IReadOnlyList<MemberAssignment>>> ListMembersAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<MemberAssignment>>($"boards/{boardId}/members", ct);

    public Task<ApiResult<Done>> SetMembersAsync(Guid boardId, IReadOnlyList<MemberAssignment> members, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"boards/{boardId}/members", new SetMembersRequest(members), None, ct);

    // ---- backlog and cards

    public Task<ApiResult<CardCreatedDto>> CreateCardAsync(Guid boardId, CardContent content, CancellationToken ct) =>
        transport.SendAsync<CardContent, CardCreatedDto>(HttpMethod.Post, $"boards/{boardId}/cards", content, None, ct);

    public Task<ApiResult<Done>> ReorderBacklogAsync(Guid boardId, BacklogOrder order, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, $"boards/{boardId}/backlog/order", order, None, ct);

    public Task<ApiResult<CardDetailDto>> GetCardAsync(Guid cardId, CancellationToken ct) =>
        transport.GetAsync<CardDetailDto>($"cards/{cardId}", ct);

    public Task<ApiResult<Done>> EditCardAsync(Guid cardId, string etag, JsonObject patch, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Patch, $"cards/{cardId}", patch, new CardPreconditions(etag), ct);

    public Task<ApiResult<Moved>> MoveCardAsync(Guid cardId, string etag, MoveCardBody body, CancellationToken ct) =>
        transport.SendAsync<MoveCardBody, Moved>(
            HttpMethod.Post, $"cards/{cardId}/move", body, new CardPreconditions(etag, Guid.NewGuid()), ct);

    public Task<ApiResult<Moved>> CancelCardAsync(Guid cardId, string etag, string reason, CancellationToken ct) =>
        transport.SendAsync<CancelCardRequest, Moved>(
            HttpMethod.Post, $"cards/{cardId}/cancel", new(reason), new CardPreconditions(etag, Guid.NewGuid()), ct);

    public Task<ApiResult<BlockResponse>> BlockCardAsync(Guid cardId, Blocker blocker, CancellationToken ct) =>
        transport.SendAsync<Blocker, BlockResponse>(HttpMethod.Post, $"cards/{cardId}/block", blocker, None, ct);

    public Task<ApiResult<BlockResponse>> UnblockCardAsync(Guid cardId, CancellationToken ct) =>
        transport.PostAsync<BlockResponse>($"cards/{cardId}/unblock", ct);

    public Task<ApiResult<Done>> ToggleChecklistItemAsync(Guid cardId, Guid itemId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"cards/{cardId}/checklist/{itemId}/toggle", ct);

    public Task<ApiResult<BreakDownResponse>> BreakDownCardAsync(Guid cardId, IReadOnlyList<string> titles, CancellationToken ct) =>
        transport.SendAsync<BreakDownRequest, BreakDownResponse>(HttpMethod.Post, $"cards/{cardId}/breakdown", new(titles), None, ct);

    public Task<ApiResult<IReadOnlyList<CommentDto>>> ListCommentsAsync(Guid cardId, CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<CommentDto>>($"cards/{cardId}/comments", ct);

    public Task<ApiResult<CommentAddedDto>> AddCommentAsync(Guid cardId, string body, CancellationToken ct) =>
        transport.SendAsync<AddCommentRequest, CommentAddedDto>(HttpMethod.Post, $"cards/{cardId}/comments", new(body), None, ct);

    // ---- GitHub

    public Task<ApiResult<IReadOnlyList<RepoLinkDto>>> ListRepositoryLinksAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<RepoLinkDto>>($"boards/{boardId}/github/links", ct);

    public Task<ApiResult<RepoLinkDto>> LinkRepositoryAsync(Guid boardId, LinkRepositoryRequest request, CancellationToken ct) =>
        transport.SendAsync<LinkRepositoryRequest, RepoLinkDto>(HttpMethod.Post, $"boards/{boardId}/github/links", request, None, ct);

    public Task<ApiResult<Done>> UnlinkRepositoryAsync(Guid boardId, long repositoryId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"boards/{boardId}/github/links/{repositoryId}", ct);

    public Task<ApiResult<Done>> LinkIssueAsync(Guid cardId, LinkIssueRequest request, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"cards/{cardId}/github/link", request, None, ct);

    public Task<ApiResult<Done>> UnlinkIssueAsync(Guid cardId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"cards/{cardId}/github/link", ct);

    public Task<ApiResult<Done>> SyncCardAsync(Guid cardId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"cards/{cardId}/github/sync", ct);

    // ---- agents and the person's GitHub connection

    public Task<ApiResult<GitHubConnectionDto>> GetGitHubConnectionAsync(CancellationToken ct) =>
        transport.GetAsync<GitHubConnectionDto>("github/connection", ct);

    /// <summary>Starts "Connect GitHub": the answer says where to send the browser.</summary>
    public Task<ApiResult<ConnectGitHubResponse>> StartGitHubConnectionAsync(CancellationToken ct) =>
        transport.GetAsync<ConnectGitHubResponse>("github/connect", ct);

    /// <summary>Hands the API what GitHub sent the browser back with.</summary>
    public Task<ApiResult<GitHubConnectionDto>> CompleteGitHubConnectionAsync(string code, string state, CancellationToken ct) =>
        transport.GetAsync<GitHubConnectionDto>($"github/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", ct);

    public Task<ApiResult<Done>> DisconnectGitHubAsync(CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, "github/connection", ct);

    public Task<ApiResult<AgentDeskDto>> GetAgentDeskAsync(Guid cardId, CancellationToken ct) =>
        transport.GetAsync<AgentDeskDto>($"cards/{cardId}/agent-dispatch", ct);

    public Task<ApiResult<AgentSessionDto>> DispatchAgentAsync(Guid cardId, DispatchAgentRequest request, CancellationToken ct) =>
        transport.SendAsync<DispatchAgentRequest, AgentSessionDto>(HttpMethod.Post, $"cards/{cardId}/agent-dispatch", request, None, ct);

    public Task<ApiResult<Done>> EndAgentSessionAsync(Guid cardId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Delete, $"cards/{cardId}/agent-dispatch", ct);

    // ---- AI: suggestions for a card, and what it noticed about a board

    public Task<ApiResult<BreakdownProposal>> ProposeBreakdownAsync(Guid cardId, CancellationToken ct) =>
        transport.PostAsync<BreakdownProposal>($"cards/{cardId}/ai/breakdown", ct);

    public Task<ApiResult<TriageSuggestion>> SuggestTriageAsync(Guid cardId, CancellationToken ct) =>
        transport.PostAsync<TriageSuggestion>($"cards/{cardId}/ai/triage", ct);

    public Task<ApiResult<DoneRuleReview>> ReviewDoneRuleAsync(Guid cardId, CancellationToken ct) =>
        transport.PostAsync<DoneRuleReview>($"cards/{cardId}/ai/done-rule-check", ct);

    public Task<ApiResult<InstructionDraft>> DraftAgentInstructionsAsync(Guid cardId, CancellationToken ct) =>
        transport.PostAsync<InstructionDraft>($"cards/{cardId}/ai/agent-instructions", ct);

    /// <summary>Says that two cards are not the same work; the suggestion goes from both.</summary>
    public Task<ApiResult<Done>> DismissDuplicateAsync(Guid cardId, Guid otherCardId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"cards/{cardId}/ai/duplicates/{otherCardId}/dismiss", ct);

    public Task<ApiResult<ForecastNarrative>> NarrateForecastAsync(Guid boardId, CancellationToken ct) =>
        transport.PostAsync<ForecastNarrative>($"boards/{boardId}/ai/forecast-narrative", ct);

    public Task<ApiResult<AiStatusDto>> GetAiStatusAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<AiStatusDto>($"boards/{boardId}/ai/status", ct);

    public Task<ApiResult<IReadOnlyList<InsightDto>>> ListInsightsAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<InsightDto>>($"boards/{boardId}/insights", ct);

    public Task<ApiResult<Done>> DismissInsightAsync(Guid boardId, long insightId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"boards/{boardId}/insights/{insightId}/dismiss", ct);

    /// <summary>Accepts a proposed WIP change: the limit changes, as the caller's own decision.</summary>
    public Task<ApiResult<Done>> AcceptInsightAsync(Guid boardId, long insightId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"boards/{boardId}/insights/{insightId}/accept", ct);

    // ---- real-time, inbox and notification settings

    public Task<ApiResult<RealtimeConnection>> NegotiateRealtimeAsync(CancellationToken ct) =>
        transport.PostAsync<RealtimeConnection>("realtime/negotiate", ct);

    public Task<ApiResult<Done>> JoinBoardAsync(Guid boardId, string connectionId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"realtime/boards/{boardId}/join", new JoinBoardRequest(connectionId), None, ct);

    public Task<ApiResult<InboxDto>> GetInboxAsync(CancellationToken ct) =>
        transport.GetAsync<InboxDto>("me/notifications", ct);

    public Task<ApiResult<Done>> MarkNotificationReadAsync(long notificationId, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"me/notifications/{notificationId}/read", ct);

    public Task<ApiResult<Done>> MarkAllNotificationsReadAsync(CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, "me/notifications/read-all", ct);

    public Task<ApiResult<Done>> SnoozeNotificationAsync(long notificationId, int hours, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Post, $"me/notifications/{notificationId}/snooze", new SnoozeRequest(hours), None, ct);

    public Task<ApiResult<Done>> SetCardMuteAsync(Guid cardId, bool isMuted, CancellationToken ct) =>
        transport.SendAsync(isMuted ? HttpMethod.Put : HttpMethod.Delete, $"cards/{cardId}/mute", ct);

    public Task<ApiResult<IReadOnlyList<NotificationPreferenceDto>>> ListNotificationPreferencesAsync(CancellationToken ct) =>
        transport.GetAsync<IReadOnlyList<NotificationPreferenceDto>>("me/notification-preferences", ct);

    public Task<ApiResult<Done>> SetNotificationPreferenceAsync(SetNotificationPreferenceRequest preference, CancellationToken ct) =>
        transport.SendAsync(HttpMethod.Put, "me/notification-preferences", preference, None, ct);

    // ---- insights

    public Task<ApiResult<ForecastDto>> GetForecastAsync(Guid boardId, CancellationToken ct) =>
        transport.GetAsync<ForecastDto>($"boards/{boardId}/metrics/forecast", ct);
}
