using System.Security.Claims;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Cards;

/// <summary>Changes one card in place: its content, its checklist and its breakdown.</summary>
public sealed class CardCommands(ICardStore cards, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const int MaxChildren = 50;

    private static readonly NotFound NoSuchCard = new("card");

    public async Task<Outcome<CardVersion>> EditAsync(CardEdit edit, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(edit.CardId, cancellationToken) is not { } card) return NoSuchCard;

        Permission granted = await authorizer.GetEffectiveAsync(user, card.BoardId, cancellationToken);
        if (!granted.AllowsEditing(isOwnCard: card.CreatedBy == user.GetObjectId()))
            return new Forbidden(Permission.CardEdit);

        if (await cards.FindContentAsync(edit.CardId, cancellationToken) is not { } current) return NoSuchCard;
        if (edit.Change(current) is not { } content) return new Invalid("The changes don't describe a card.");
        if (content.FindProblems() is { Count: > 0 } problems) return new Invalid(problems);

        return await cards.UpdateAsync(edit.CardId, edit.ExpectedRowVersion, content, StampFor(user), cancellationToken);
    }


    public async Task<Outcome<Saved>> ToggleChecklistItemAsync(
        Guid cardId, Guid itemId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardEdit, cancellationToken) is { } denied)
            return denied;

        bool toggled = await cards.ToggleChecklistItemAsync(cardId, itemId, StampFor(user), cancellationToken);
        return toggled ? Saved.Instance : new NotFound("checklist item");
    }

    /// <summary>FR-C03: turns an item into child tasks while it sits in the breakdown step.</summary>
    public async Task<Outcome<IReadOnlyList<Guid>>> BreakDownAsync(
        Guid cardId, IReadOnlyList<string> titles, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(cardId, cancellationToken) is not { } card) return NoSuchCard;
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardEdit, cancellationToken) is { } denied)
            return denied;
        if (FindBreakdownProblem(card, titles) is { } problem)
            return new Invalid(problem);

        return new Outcome<IReadOnlyList<Guid>>(
            await cards.BreakDownAsync(cardId, [.. titles.Select(title => title.Trim())], StampFor(user), cancellationToken));
    }

    private Stamp StampFor(ClaimsPrincipal user) => new(user.GetObjectId(), clock.GetUtcNow());

    private static string? FindBreakdownProblem(CardFacts card, IReadOnlyList<string> titles) =>
        !card.IsInBreakdownStep ? "A card is broken down while it sits in the breakdown step."
        : titles.Count is 0 or > MaxChildren ? $"A breakdown needs 1 to {MaxChildren} tasks."
        : titles.Any(title => string.IsNullOrWhiteSpace(title) || title.Length > CardContent.MaxTitleLength)
            ? $"Each task needs a title of 1 to {CardContent.MaxTitleLength} characters."
        : null;
}
