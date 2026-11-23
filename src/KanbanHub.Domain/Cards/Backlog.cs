using System.Security.Claims;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Cards;

/// <summary>Fills and arranges a board's backlog: new cards, the Ready list and the priority buckets (FR-B04).</summary>
public sealed class Backlog(ICardStore cards, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public async Task<Outcome<CardCreated>> AddAsync(
        Guid boardId, CardContent content, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.BacklogCreate, cancellationToken) is { } denied)
            return denied;
        if (content.FindProblems() is { Count: > 0 } problems)
            return new Invalid(problems);

        CardCreated? created = await cards.CreateAsync(boardId, content, StampFor(user), cancellationToken);
        return created is null ? new NotFound("board") : created;
    }

    public async Task<Outcome<Saved>> ReorderAsync(
        Guid boardId, BacklogOrder order, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await authorizer.DenyUnlessAsync(user, boardId, Permission.BacklogOrder, cancellationToken) is { } denied)
            return denied;
        if (FindProblem(order) is { } problem)
            return new Invalid(problem);

        bool stored = await cards.ReorderBacklogAsync(boardId, order, StampFor(user), cancellationToken);
        return stored ? Saved.Instance : new Invalid("The Ready list is longer than this board allows.");
    }

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());

    private static string? FindProblem(BacklogOrder order) =>
        order.Ready.Distinct().Count() != order.Ready.Count ? "A card can appear in the Ready list only once."
        : order.Buckets.Any(placement => placement.Bucket > CardContent.LowestPriorityBucket)
            ? $"The priority bucket must be 0 to {CardContent.LowestPriorityBucket}."
        : null;
}
