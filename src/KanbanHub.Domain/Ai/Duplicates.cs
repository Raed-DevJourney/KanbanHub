using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Ai;

/// <summary>A text as a vector, with the model that made it and what making it cost.</summary>
public sealed record class TextEmbedding(float[] Vector, string Model, int TokensUsed);

/// <summary>
/// Turns text into a vector: an embedding deployment in Azure, a stand-in elsewhere. Answers null when there is
/// no model here or it gave a vector of the wrong size.
/// </summary>
public interface IEmbedder
{
    Task<TextEmbedding?> EmbedAsync(string text, CancellationToken cancellationToken);
}

/// <summary>What the finder reads of a card: its text, and the hash of the text its stored embedding was made from.</summary>
public sealed record class CardText(Guid BoardId, string Title, string? Description, byte[]? EmbeddedHash);

/// <summary>A card near another one, by the cosine distance of their embeddings (0 is identical).</summary>
public sealed record class NearCard(Guid CardId, double Distance)
{
    public int SimilarityPercent => (int)Math.Clamp(Math.Round((1 - Distance) * 100), 0, 100);
}

/// <summary>Storage port for the duplicate finder: embeddings in a <c>vector</c> column, compared by the database.</summary>
public interface IDuplicateStore
{
    Task<CardText?> FindTextAsync(Guid cardId, CancellationToken cancellationToken);

    Task SaveEmbeddingAsync(Guid cardId, TextEmbedding embedding, byte[] textHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The nearest cards of the same board within <paramref name="maxDistance"/>, nearest first.</summary>
    Task<IReadOnlyList<NearCard>> FindNearestAsync(Guid cardId, double maxDistance, int take, CancellationToken cancellationToken);

    /// <summary>Makes these the open suggestions found from the card; a pair someone dismissed stays dismissed.</summary>
    Task SaveSuggestionsAsync(Guid cardId, IReadOnlyList<NearCard> duplicates, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns false when there was no such open suggestion.</summary>
    Task<bool> DismissAsync(Guid cardId, Guid otherCardId, Stamp stamp, CancellationToken cancellationToken);
}

/// <summary>What counts as a possible duplicate, and what of a card is compared.</summary>
public static class DuplicateRule
{
    /// <summary>The size of the stored vectors (<c>vector(1536)</c>); the embedding deployment must match it.</summary>
    public const int Dimensions = 1536;

    /// <summary>Cards at most this far apart are suggested: at least 85% alike.</summary>
    public const double MaxDistance = 0.15;

    public const int MaxSuggestions = 3;
    public const int MaxTextLength = 8000;

    public static string TextOf(CardText card)
    {
        string text = $"{card.Title}\n\n{card.Description}".Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    public static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// The duplicate finder (section 11.1, feature 8). When a card is created or its text changes on a board that
/// uses AI, its text is embedded and the nearest cards of the board become "possible duplicate" suggestions on
/// it. Card text only ever goes to the embedding model, which answers with numbers; nothing here writes text.
/// </summary>
public sealed class DuplicateFinder(
    IDuplicateStore store, IEmbedder embedder, AiRunner gate, IAiPolicyStore usage, TimeProvider clock) : IBoardEventConsumer
{
    public async Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        if (boardEvent.Type is not (BoardEvent.CardCreated or BoardEvent.CardUpdated)) return;
        if (await store.FindTextAsync(boardEvent.CardId, cancellationToken) is not { } card) return;
        if (!await EmbedAsync(boardEvent.CardId, card, cancellationToken)) return;

        IReadOnlyList<NearCard> near = await store.FindNearestAsync(
            boardEvent.CardId, DuplicateRule.MaxDistance, DuplicateRule.MaxSuggestions, cancellationToken);
        await store.SaveSuggestionsAsync(boardEvent.CardId, near, clock.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Stores a fresh embedding of the card's text. False when there is nothing new to compare: the text is the
    /// one already embedded (most card events don't touch it), the board may not use AI, or no vector came back.
    /// </summary>
    private async Task<bool> EmbedAsync(Guid cardId, CardText card, CancellationToken cancellationToken)
    {
        string text = DuplicateRule.TextOf(card);
        byte[] hash = DuplicateRule.Hash(text);
        if (card.EmbeddedHash is { } embedded && embedded.AsSpan().SequenceEqual(hash)) return false;
        if (await gate.FindRefusalAsync(card.BoardId, cancellationToken) is not null) return false;
        if (await embedder.EmbedAsync(text, cancellationToken) is not { } embedding) return false;

        DateTimeOffset now = clock.GetUtcNow();
        await usage.RecordUsageAsync(card.BoardId, AiFeature.Duplicates, embedding.TokensUsed, now, cancellationToken);
        await store.SaveEmbeddingAsync(cardId, embedding, hash, now, cancellationToken);
        return true;
    }
}

/// <summary>What people do with a duplicate suggestion: say that the two cards are not the same work.</summary>
public sealed class DuplicateDesk(IDuplicateStore store, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public async Task<Outcome<Saved>> DismissAsync(Guid cardId, Guid otherCardId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await store.FindTextAsync(cardId, cancellationToken) is not { } card) return new NotFound("card");
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.CardEdit, cancellationToken) is { } denied) return denied;

        var stamp = new Stamp(user.GetObjectId(), clock.GetUtcNow());
        return await store.DismissAsync(cardId, otherCardId, stamp, cancellationToken) ? Saved.Instance : new NotFound("duplicate suggestion");
    }
}
