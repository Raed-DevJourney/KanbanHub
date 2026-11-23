using System.Data;
using KanbanHub.Domain;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Stores a card's content and flags through the write procedures.</summary>
public sealed class CardStore(ISqlConnectionFactory database) : ICardStore
{
    private const string FactsSql = """
        SELECT c.BoardId, c.CreatedBy, ISNULL(s.CountsParentsOnly, 0)
        FROM kb.Card c
        LEFT JOIN kb.BoardStep s ON s.StepId = c.StepId
        WHERE c.CardId = @CardId AND c.IsDeleted = 0;
        """;

    private const string ContentSql = """
        SELECT Title, [Description], CardType, PriorityBucket, Origin, DueDate, TargetRelease, EstimatedTasks
        FROM kb.Card
        WHERE CardId = @CardId AND IsDeleted = 0;
        """;

    public async Task<CardFacts?> FindFactsAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(FactsSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.FirstOrDefaultAsync(
            command, reader => new CardFacts(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2)), cancellationToken);
    }

    public async Task<CardContent?> FindContentAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(ContentSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.FirstOrDefaultAsync(command, ReadContent, cancellationToken);
    }

    public async Task<CardCreated?> CreateAsync(Guid boardId, CardContent content, Stamp stamp, CancellationToken cancellationToken)
    {
        Guid cardId = Guid.NewGuid();
        await using SqlCommand command = Sql.Procedure("kb.usp_CreateCard");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddGuid("@BoardId", boardId);
        AddContent(command.Parameters, content);
        command.Parameters.AddStamp(stamp);

        return await database.SingleAsync(command, reader => ReadCreated(reader, cardId), cancellationToken);
    }

    public async Task<Outcome<CardVersion>> UpdateAsync(
        Guid cardId, byte[] expectedRowVersion, CardContent content, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_UpdateCard");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddBinary("@ExpectedRowVersion", expectedRowVersion, size: 8);
        AddContent(command.Parameters, content);
        command.Parameters.AddStamp(stamp);

        return await database.SingleAsync(command, ReadUpdateOutcome, cancellationToken);
    }

    public async Task<CardVersion> SetBlockedAsync(Guid cardId, Blocker? blocker, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetCardBlocked");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddValue("@IsBlocked", SqlDbType.Bit, blocker is not null);
        command.Parameters.AddText("@Reason", blocker?.Reason, size: 500);
        command.Parameters.AddTinyInt("@Category", (byte?)blocker?.Category);
        command.Parameters.AddStamp(stamp);

        return await database.SingleAsync(command, reader => new CardVersion(reader.GetFieldValue<byte[]>(0)), cancellationToken);
    }

    public async Task<bool> ToggleChecklistItemAsync(Guid cardId, Guid itemId, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_ToggleChecklistItem");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddGuid("@ItemId", itemId);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<Guid>> BreakDownAsync(
        Guid parentCardId, IReadOnlyList<string> titles, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_BreakDownCard");
        command.Parameters.AddGuid("@ParentCardId", parentCardId);
        command.Parameters.AddJson("@TitlesJson", titles);
        command.Parameters.AddStamp(stamp);

        return await database.ListAsync(command, reader => reader.GetGuid(0), cancellationToken);
    }

    public async Task<bool> ReorderBacklogAsync(Guid boardId, BacklogOrder order, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_ReorderBacklog");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddJson("@ReadyJson", order.Ready);
        command.Parameters.AddJson("@BucketsJson", order.Buckets);
        command.Parameters.AddStamp(stamp);

        return await database.ResultCodeAsync(command, cancellationToken) == ResultCodes.Ok;
    }

    private static void AddContent(SqlParameterCollection parameters, CardContent content)
    {
        parameters.AddText("@Title", content.Title.Trim(), size: CardContent.MaxTitleLength);
        parameters.AddText("@Description", content.Description);
        parameters.AddTinyInt("@CardType", (byte)content.CardType);
        parameters.AddTinyInt("@PriorityBucket", content.PriorityBucket);
        parameters.AddValue("@Origin", SqlDbType.VarChar, content.Origin, size: 3);
        parameters.AddValue("@DueDate", SqlDbType.Date, content.DueDate?.ToDateTime(TimeOnly.MinValue));
        parameters.AddText("@TargetRelease", content.TargetRelease, size: CardContent.MaxTargetReleaseLength);
        parameters.AddValue("@EstimatedTasks", SqlDbType.SmallInt, content.EstimatedTasks);
    }

    private static CardContent ReadContent(SqlDataReader reader) => new()
    {
        Title = reader.GetString(0),
        Description = reader.StringOrNull(1),
        CardType = (CardType)reader.GetByte(2),
        PriorityBucket = reader.ByteOrNull(3),
        Origin = reader.StringOrNull(4),
        DueDate = reader.DateOrNull(5),
        TargetRelease = reader.StringOrNull(6),
        EstimatedTasks = reader.Int16OrNull(7)
    };

    private static CardCreated? ReadCreated(SqlDataReader reader, Guid cardId) =>
        reader.GetInt32(0) == ResultCodes.Ok
            ? new CardCreated(cardId, reader.GetInt32(1), reader.GetFieldValue<byte[]>(2))
            : null;

    private static Outcome<CardVersion> ReadUpdateOutcome(SqlDataReader reader) => reader.GetInt32(0) switch
    {
        ResultCodes.Ok => new CardVersion(reader.GetFieldValue<byte[]>(1)),
        ResultCodes.Conflict => new ConcurrencyConflict(reader.GetFieldValue<byte[]>(1)),
        _ => new NotFound("card")
    };
}
