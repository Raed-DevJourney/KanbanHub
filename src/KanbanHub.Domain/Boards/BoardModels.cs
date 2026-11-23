using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Boards;

public sealed record class Workspace(Guid Id, string Name);

/// <summary>What a caller supplies to create a board from a template.</summary>
public sealed record class CreateBoardCommand(Guid WorkspaceId, string Name, string TemplateKey, string TimeZoneId);

/// <summary>A board ready to be stored: the template turned into concrete steps, owned by its creator.</summary>
public sealed record class NewBoard
{
    public Guid Id { get; } = Guid.NewGuid();

    public required Guid WorkspaceId { get; init; }

    public required string Name { get; init; }

    public required string TimeZoneId { get; init; }

    public required BoardTemplate Template { get; init; }

    public required Guid OwnerId { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>A step's limit as a caller submits it; validated before it becomes a <see cref="WipLimit"/>.</summary>
public sealed record class StepLimit(Guid StepId, int MaxItems, WipPolicy Policy);

/// <summary>All of a board's step limits, with why they changed (rule R8).</summary>
public sealed record class WipLimitChange(Guid BoardId, IReadOnlyList<StepLimit> Limits, string? Reason);

public sealed record class DoneRuleItemDraft(string Text, bool IsRequired);

/// <summary>A new version of a step's done rule; a card type narrows it to that type only.</summary>
public sealed record class DoneRuleChange(Guid BoardId, Guid StepId, byte? CardType, IReadOnlyList<DoneRuleItemDraft> Items);

public enum PrincipalType : byte
{
    User = 1,
    Group = 2,
    ServicePrincipal = 3
}

/// <summary>An Entra user or group given a role on a board.</summary>
public sealed record class MemberAssignment(Guid PrincipalId, PrincipalType PrincipalType, short RoleId);

/// <summary>Who did something and when; every stored change carries one.</summary>
public sealed record class Stamp(Guid ActorId, DateTimeOffset Now);
