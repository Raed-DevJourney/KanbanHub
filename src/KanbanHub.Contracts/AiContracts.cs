using KanbanHub.Domain.Ai;

namespace KanbanHub.Contracts;

/// <summary>
/// <c>GET /boards/{id}/ai/status</c>: whether this environment has a model, whether the board opted in, and what it
/// spent of its monthly budget.
/// </summary>
public sealed record AiStatusDto(bool IsConfigured, bool IsEnabled, int MonthlyTokenBudget, long TokensUsedThisMonth)
{
    public bool IsOverBudget => TokensUsedThisMonth >= MonthlyTokenBudget;
}

/// <summary>A card that looks like the same work as the one being shown, found by comparing their texts.</summary>
public sealed record DuplicateDto(Guid CardId, int Number, string Title, int SimilarityPercent);

/// <summary>
/// One line of <c>GET /boards/{id}/insights</c>. Every kind has the same shape: what it says, the points behind
/// it, and the numbers of the cards it cites (all of them cards the model was shown).
/// </summary>
public sealed record InsightDto
{
    public required long Id { get; init; }

    public required AiFeature Kind { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<string> Points { get; init; } = [];

    public IReadOnlyList<int> Cards { get; init; } = [];

    /// <summary>Set on WIP advice: the change that accepting the insight makes.</summary>
    public WipProposal? Proposal { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}
