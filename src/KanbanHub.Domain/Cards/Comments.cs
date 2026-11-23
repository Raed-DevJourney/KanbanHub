using System.Security.Claims;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Cards;

public sealed record class CommentAdded(Guid CommentId);

/// <summary>Where a comment came from when it wasn't written here: its id on GitHub and who wrote it there.</summary>
public sealed record class ExternalComment(long GitHubCommentId, string AuthorLogin);

/// <summary>A comment to add to a card. <see cref="Source"/> is set when it was written on GitHub.</summary>
public sealed record class CommentDraft(Guid CardId, string? Body)
{
    public ExternalComment? Source { get; init; }
}

/// <summary>Storage port for card comments.</summary>
public interface ICommentStore
{
    /// <summary>Returns the new comment's id, or null when that GitHub comment was stored before.</summary>
    Task<Guid?> AddAsync(CommentDraft comment, Stamp stamp, CancellationToken cancellationToken);
}

/// <summary>
/// Adds Markdown comments to cards. The text is stored as written and treated as untrusted:
/// whatever renders it must sanitize it.
/// </summary>
public sealed class Comments(ICardStore cards, ICommentStore comments, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const int MaxLength = 10_000;

    public Task<Outcome<CommentAdded>> AddAsync(
        Guid cardId, string? body, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        AddAsync(new CommentDraft(cardId, body), user, cancellationToken);

    public async Task<Outcome<CommentAdded>> AddAsync(CommentDraft draft, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(draft.CardId, cancellationToken) is not { } card) return new NotFound("card");
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CommentCreate, cancellationToken) is { } denied)
            return denied;
        if (string.IsNullOrWhiteSpace(draft.Body) || draft.Body.Length > MaxLength)
            return new Invalid($"A comment needs 1 to {MaxLength} characters.");

        var stamp = new Stamp(user.GetObjectId(), clock.GetUtcNow());
        Guid? stored = await comments.AddAsync(draft with { Body = draft.Body.Trim() }, stamp, cancellationToken);
        return stored is { } commentId ? new CommentAdded(commentId) : new Invalid("That comment is on the card already.");
    }
}
