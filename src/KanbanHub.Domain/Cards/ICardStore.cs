using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Cards;

/// <summary>What the rules need to know about a card before changing it.</summary>
public sealed record class CardFacts(Guid BoardId, Guid CreatedBy, bool IsInBreakdownStep);

/// <summary>A card's row version after a change: the next <c>ETag</c>.</summary>
public sealed record class CardVersion(byte[] RowVersion);

public sealed record class CardCreated(Guid CardId, int CardNumber, byte[] RowVersion);

public sealed record class Blocker(string Reason, BlockerCategory Category);

/// <summary>An edit to a card's content: given what the card holds now, <paramref name="Change"/> returns what it should hold (null when the change makes no sense).</summary>
public sealed record class CardEdit(Guid CardId, byte[] ExpectedRowVersion, Func<CardContent, CardContent?> Change);

public sealed record class BucketPlacement(Guid CardId, byte Bucket);

/// <summary>The backlog as the flow manager arranged it: the ordered Ready list, and cards moved between buckets.</summary>
public sealed record class BacklogOrder(IReadOnlyList<Guid> Ready, IReadOnlyList<BucketPlacement> Buckets);

/// <summary>Storage port for a card's content and flags. Moves go through <see cref="ICardRepository"/>.</summary>
public interface ICardStore
{
    Task<CardFacts?> FindFactsAsync(Guid cardId, CancellationToken cancellationToken);

    Task<CardContent?> FindContentAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>Adds a card to the board's backlog. Returns null when the board doesn't exist.</summary>
    Task<CardCreated?> CreateAsync(Guid boardId, CardContent content, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Replaces the card's content if it hasn't changed since <paramref name="expectedRowVersion"/>.</summary>
    Task<Outcome<CardVersion>> UpdateAsync(
        Guid cardId, byte[] expectedRowVersion, CardContent content, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Blocks the card, or unblocks it when <paramref name="blocker"/> is null.</summary>
    Task<CardVersion> SetBlockedAsync(Guid cardId, Blocker? blocker, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Ticks or unticks a done-rule item. Returns false when the card has no such item.</summary>
    Task<bool> ToggleChecklistItemAsync(Guid cardId, Guid itemId, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Creates child tasks beside their parent and returns their ids.</summary>
    Task<IReadOnlyList<Guid>> BreakDownAsync(
        Guid parentCardId, IReadOnlyList<string> titles, Stamp stamp, CancellationToken cancellationToken);

    /// <summary>Returns false when the Ready list would exceed the board's cap.</summary>
    Task<bool> ReorderBacklogAsync(Guid boardId, BacklogOrder order, Stamp stamp, CancellationToken cancellationToken);
}
