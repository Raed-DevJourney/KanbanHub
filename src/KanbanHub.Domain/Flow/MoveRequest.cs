namespace KanbanHub.Domain.Flow;

/// <summary>A move that passed the permission and legality gates, ready for the enforcing procedure.</summary>
public sealed record class MoveRequest
{
    public required Guid CardId { get; init; }

    public required byte[] ExpectedRowVersion { get; init; }

    public required CardPosition To { get; init; }

    public decimal? ToRank { get; init; }

    public required Guid ActorId { get; init; }

    public required MoveSource Source { get; init; }

    public required Guid IdempotencyKey { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>
    /// Non-null only when the actor holds <c>wip.override</c>: the procedure trusts it
    /// and lets the move exceed "Allow with reason" limits.
    /// </summary>
    public string? OverrideReason { get; init; }

    /// <summary>Why the card leaves the normal flow: required for backward moves and for canceling.</summary>
    public string? Reason { get; init; }
}
