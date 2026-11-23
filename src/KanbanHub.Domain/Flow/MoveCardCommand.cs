namespace KanbanHub.Domain.Flow;

/// <summary>A caller's request to move a card.</summary>
public sealed record class MoveCardCommand
{
    public required Guid CardId { get; init; }

    /// <summary>The card's row version as the caller last saw it (the <c>If-Match</c> ETag).</summary>
    public required byte[] ExpectedRowVersion { get; init; }

    /// <summary>The target step; ignored for locations outside a step.</summary>
    public Guid? ToStepId { get; init; }

    public required Location ToLocation { get; init; }

    public decimal? ToRank { get; init; }

    /// <summary>
    /// Why the caller overrides the normal flow: required to move backward, and to exceed
    /// an "Allow with reason" WIP limit. Blank text counts as no reason.
    /// </summary>
    public string? Reason
    {
        get;
        init => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Makes a retried request return the original result (rule R10).</summary>
    public required Guid IdempotencyKey { get; init; }

    /// <summary>Who asks: a person unless said otherwise. Every source passes the same gates (rule R11).</summary>
    public MoveSource Source { get; init; } = MoveSource.User;
}
