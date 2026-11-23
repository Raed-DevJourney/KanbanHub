using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Contracts;

/// <summary><c>GET /me</c>: who the caller is and which tenant-wide roles they hold.</summary>
public sealed record MeResponse(Guid UserId, string? Name, IReadOnlyList<string> TenantRoles);

/// <summary><c>GET /boards/{id}/me</c>: the caller's effective permissions, which decide what the UI renders.</summary>
public sealed record BoardAccessResponse(IReadOnlyList<string> Permissions);

public sealed record WorkspaceDto(Guid Id, string Name);

public sealed record CreateWorkspaceRequest(string Name);

public sealed record BoardSummaryDto(Guid Id, Guid WorkspaceId, string Name);

public sealed record CreateBoardRequest(Guid WorkspaceId, string Name, string TemplateKey, string TimeZoneId);

public sealed record DoneRuleItemDto(string Text, bool IsRequired);

/// <summary>A workflow step with its policies and its current load, as a column header shows it.</summary>
public sealed record StepDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>1-based position in the workflow.</summary>
    public required int Order { get; init; }

    public required string Color { get; init; }

    public bool HasTrack { get; init; }

    public bool IsFinal { get; init; }

    public bool CountsParentsOnly { get; init; }

    /// <summary>Cards in this step may be sent to coding agents.</summary>
    public bool IsAgentEligible { get; init; }

    /// <summary>Null when the step has no limit.</summary>
    public int? WipLimit { get; init; }

    public WipPolicy? Policy { get; init; }

    /// <summary>Cards counted toward the limit right now (rules R1 and R2). A preview: the server decides each move.</summary>
    public int WipCount { get; init; }

    public IReadOnlyList<DoneRuleItemDto> DoneRule { get; init; } = [];
}

/// <summary><c>GET /boards/{id}</c>: everything the board screen needs in one response.</summary>
public sealed record BoardSnapshotDto
{
    public required Guid Id { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required string Name { get; init; }

    public required int WorkflowVersion { get; init; }

    public short? ReadyListCap { get; init; }

    /// <summary>How many agent sessions may be open on the board at once (section 10.2).</summary>
    public int AgentConcurrencyLimit { get; init; }

    /// <summary>The board opted in to AI (section 11.2); the AI buttons appear only then.</summary>
    public bool AiEnabled { get; init; }

    public required IReadOnlyList<StepDto> Steps { get; init; }

    public required IReadOnlyList<CardDto> Cards { get; init; }

    /// <summary>Every color token with the board's overrides applied; the UI emits them as CSS custom properties.</summary>
    public IReadOnlyDictionary<string, string> Palette { get; init; } = new Dictionary<string, string>();
}

public sealed record SetWipLimitsRequest(IReadOnlyList<StepLimit> Limits, string? Reason);

public sealed record WipCalculatorRequest(IReadOnlyList<StepCapacityDraft> Steps);

public sealed record SetDoneRuleRequest(IReadOnlyList<DoneRuleItemDraft> Items, byte? CardType = null);

public sealed record SetMembersRequest(IReadOnlyList<MemberAssignment> Members);

public sealed record SetWorkflowRequest(IReadOnlyList<StepDraft> Steps);

public sealed record SetPaletteRequest(IReadOnlyList<PaletteColor> Colors);

/// <summary>One line of a board's audit trail: a card move or any other audited change.</summary>
public sealed record AuditEntryDto
{
    public required DateTime OccurredAtUtc { get; init; }

    public required Guid ActorId { get; init; }

    /// <summary>card.moved, card.field, card.blocked, wip.limit, done-rule, members, workflow, palette, board.settings, ...</summary>
    public required string EventType { get; init; }

    public Guid? CardId { get; init; }

    public string? Field { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? Reason { get; init; }
}

/// <summary><c>GET /boards/{id}/metrics/forecast</c>: the book's forecast for one priority bucket.</summary>
public sealed record ForecastDto
{
    public required int WindowDays { get; init; }

    /// <summary>The bucket forecast (P0 by default); null when every bucket is included.</summary>
    public byte? PriorityBucket { get; init; }

    /// <summary>TCR: tasks finished per day.</summary>
    public required double CompletionPerDay { get; init; }

    /// <summary>TAR: tasks added per day.</summary>
    public required double AddPerDay { get; init; }

    /// <summary>CTE: tasks still to do.</summary>
    public required int CurrentTaskEstimate { get; init; }

    /// <summary>CTE / (TCR - TAR); absent when the work never finishes at these rates.</summary>
    public double? DaysRemaining { get; init; }

    /// <summary>TAR is at least TCR and there is work left: show the red warning.</summary>
    public bool NeverFinishes { get; init; }
}
