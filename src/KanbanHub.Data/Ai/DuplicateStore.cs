using System.Data;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Boards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Ai;

/// <summary>
/// Embeddings in <c>kb.CardEmbedding</c> and the suggestions drawn from them in <c>kb.CardDuplicate</c>. A vector
/// travels to SQL as a JSON array parameter and is cast to <c>vector</c> there; it is compared in the database
/// with <c>VECTOR_DISTANCE</c> and never read back.
/// </summary>
public sealed class DuplicateStore(ISqlConnectionFactory database) : IDuplicateStore
{
    private const string TextSql = """
        SELECT c.BoardId, c.Title, c.[Description], e.TextHash
        FROM kb.Card c
        LEFT JOIN kb.CardEmbedding e ON e.CardId = c.CardId
        WHERE c.CardId = @CardId AND c.IsDeleted = 0;
        """;

    /// <summary>The row the save procedure reads with <c>OPENJSON</c>.</summary>
    private sealed record SuggestionRow(Guid CardId, int SimilarityPercent);

    public async Task<CardText?> FindTextAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(TextSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    public async Task SaveEmbeddingAsync(
        Guid cardId, TextEmbedding embedding, byte[] textHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SaveCardEmbedding");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddJson("@EmbeddingJson", embedding.Vector);
        command.Parameters.AddValue("@Model", SqlDbType.VarChar, embedding.Model, size: 100);
        command.Parameters.AddBinary("@TextHash", textHash, size: 32);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<NearCard>> FindNearestAsync(Guid cardId, double maxDistance, int take, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_FindNearestCards");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddValue("@MaxDistance", SqlDbType.Float, maxDistance);
        command.Parameters.AddValue("@Take", SqlDbType.Int, take);

        return await database.ListAsync(command, reader => new NearCard(reader.GetGuid(0), reader.GetDouble(1)), cancellationToken);
    }

    public async Task SaveSuggestionsAsync(
        Guid cardId, IReadOnlyList<NearCard> duplicates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SaveCardDuplicates");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddJson("@DuplicatesJson", duplicates.Select(near => new SuggestionRow(near.CardId, near.SimilarityPercent)));
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<bool> DismissAsync(Guid cardId, Guid otherCardId, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_DismissCardDuplicate");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddGuid("@OtherCardId", otherCardId);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }

    private static CardText Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.StringOrNull(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3));
}
