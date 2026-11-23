using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Planning;

namespace KanbanHub.Contracts;

/// <summary>
/// Source-generated JSON for every type that crosses the API boundary: camelCase names,
/// enums as their names, nulls omitted.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(BoardAccessResponse))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceDto>))]
[JsonSerializable(typeof(CreateWorkspaceRequest))]
[JsonSerializable(typeof(IReadOnlyList<BoardSummaryDto>))]
[JsonSerializable(typeof(CreateBoardRequest))]
[JsonSerializable(typeof(BoardSnapshotDto))]
[JsonSerializable(typeof(SetWipLimitsRequest))]
[JsonSerializable(typeof(WipCalculatorRequest))]
[JsonSerializable(typeof(IReadOnlyList<WipSuggestion>))]
[JsonSerializable(typeof(SetDoneRuleRequest))]
[JsonSerializable(typeof(SetMembersRequest))]
[JsonSerializable(typeof(CardContent))]
[JsonSerializable(typeof(CardCreatedDto))]
[JsonSerializable(typeof(CardDetailDto))]
[JsonSerializable(typeof(MoveCardBody))]
[JsonSerializable(typeof(Moved))]
[JsonSerializable(typeof(Blocker))]
[JsonSerializable(typeof(BlockResponse))]
[JsonSerializable(typeof(CancelCardRequest))]
[JsonSerializable(typeof(BreakDownRequest))]
[JsonSerializable(typeof(BreakDownResponse))]
[JsonSerializable(typeof(BacklogOrder))]
[JsonSerializable(typeof(IReadOnlyList<WipViolation>))]
[JsonSerializable(typeof(IReadOnlyList<Guid>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(InboxDto))]
[JsonSerializable(typeof(IReadOnlyList<NotificationPreferenceDto>))]
[JsonSerializable(typeof(SetNotificationPreferenceRequest))]
[JsonSerializable(typeof(SnoozeRequest))]
[JsonSerializable(typeof(JoinBoardRequest))]
[JsonSerializable(typeof(KanbanHub.Domain.Events.RealtimeConnection))]
[JsonSerializable(typeof(KanbanHub.Domain.Events.BoardChanged))]
[JsonSerializable(typeof(SetWorkflowRequest))]
[JsonSerializable(typeof(IReadOnlyList<KanbanHub.Domain.Boards.MemberAssignment>))]
[JsonSerializable(typeof(IReadOnlyList<CommentDto>))]
[JsonSerializable(typeof(SetPaletteRequest))]
[JsonSerializable(typeof(KanbanHub.Domain.Boards.BoardPreferences))]
[JsonSerializable(typeof(IReadOnlyList<AuditEntryDto>))]
[JsonSerializable(typeof(ForecastDto))]
[JsonSerializable(typeof(AddCommentRequest))]
[JsonSerializable(typeof(CommentAddedDto))]
[JsonSerializable(typeof(IReadOnlyList<RepoLinkDto>))]
[JsonSerializable(typeof(RepoLinkDto))]
[JsonSerializable(typeof(LinkRepositoryRequest))]
[JsonSerializable(typeof(LinkIssueRequest))]
[JsonSerializable(typeof(GitHubIssueDto))]
[JsonSerializable(typeof(GitHubConnectionDto))]
[JsonSerializable(typeof(ConnectGitHubResponse))]
[JsonSerializable(typeof(AgentDeskDto))]
[JsonSerializable(typeof(AgentSessionDto))]
[JsonSerializable(typeof(DispatchAgentRequest))]
[JsonSerializable(typeof(AiStatusDto))]
[JsonSerializable(typeof(IReadOnlyList<InsightDto>))]
[JsonSerializable(typeof(KanbanHub.Domain.Ai.BreakdownProposal))]
[JsonSerializable(typeof(KanbanHub.Domain.Ai.TriageSuggestion))]
[JsonSerializable(typeof(KanbanHub.Domain.Ai.DoneRuleReview))]
[JsonSerializable(typeof(KanbanHub.Domain.Ai.InstructionDraft))]
[JsonSerializable(typeof(KanbanHub.Domain.Ai.ForecastNarrative))]
public sealed partial class KanbanHubJsonContext : JsonSerializerContext;
