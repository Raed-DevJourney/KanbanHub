using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Web.Tests.Infrastructure;

/// <summary>A three-step board to test against: Specify, Implement (limit 2, with Track) and the final Validate.</summary>
internal static class SampleBoard
{
    public static readonly Guid Id = Guid.NewGuid();

    public static readonly StepDto Specify = Step("Specify", order: 1, limit: null);
    public static readonly StepDto Implement = Step("Implement", order: 2, limit: 2) with { HasTrack = true };
    public static readonly StepDto Validate = Step("Validate", order: 3, limit: null) with { IsFinal = true };

    public static BoardSnapshotDto With(params CardDto[] cards) => With(WipPolicy.Strict, cards);

    public static BoardSnapshotDto With(WipPolicy implementPolicy, params CardDto[] cards) => new()
    {
        Id = Id,
        WorkspaceId = Guid.NewGuid(),
        Name = "Flow",
        WorkflowVersion = 1,
        Steps = [Specify, Implement with { Policy = implementPolicy }, Validate],
        Cards = cards,
        Palette = Palette.Defaults
    };

    public static CardDto Card(string title, StepDto? step, Location location) => new()
    {
        Id = Guid.NewGuid(),
        Number = Math.Abs(title.GetHashCode(StringComparison.Ordinal) % 1000),
        Title = title,
        StepId = step?.Id,
        Location = location,
        Rank = 1,
        ETag = "AAAAAAAAAAE="
    };

    private static StepDto Step(string name, int order, int? limit) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Order = order,
        Color = "#2563EB",
        WipLimit = limit,
        Policy = limit is null ? null : WipPolicy.Strict
    };
}
