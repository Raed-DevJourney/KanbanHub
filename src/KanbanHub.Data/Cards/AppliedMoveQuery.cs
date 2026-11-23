using System.Text.Json;
using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Finds the movement an idempotency key already produced, so a retry can be answered as the original was.</summary>
internal static class AppliedMoveQuery
{
    private const string Sql = """
        SELECT m.MovementId, m.BoardId, CAST(c.[RowVersion] AS binary(8)), m.ViolationsJson
        FROM kb.CardMovement m
        JOIN kb.Card c ON c.CardId = m.CardId
        WHERE m.IdempotencyKey = @IdempotencyKey AND m.CardId = @CardId;
        """;

    /// <summary>A row of <c>kb.CardMovement.ViolationsJson</c>, as <c>kb.usp_MoveCard</c> writes it.</summary>
    private sealed record RecordedViolation(byte Scope, string ScopeName, int MaxItems, int CurrentCount, byte Policy);

    public static async Task<Moved?> ExecuteAsync(
        ISqlConnectionFactory database, Guid cardId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Data.Sql.Query(Sql);
        command.Parameters.AddGuid("@IdempotencyKey", idempotencyKey);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.FirstOrDefaultAsync(command, reader => Read(reader, cardId), cancellationToken);
    }

    private static Moved Read(SqlDataReader reader, Guid cardId) => new(
        cardId,
        BoardId: reader.GetGuid(1),
        MovementId: reader.GetInt64(0),
        RowVersion: reader.GetFieldValue<byte[]>(2),
        Recorded: ReadViolations(reader.StringOrNull(3)));

    private static List<WipViolation> ReadViolations(string? json) =>
    [
        .. (json is null ? [] : JsonSerializer.Deserialize<List<RecordedViolation>>(json) ?? [])
            .Select(v => new WipViolation((WipScope)v.Scope, v.ScopeName, v.MaxItems, v.CurrentCount, (WipPolicy)v.Policy))
    ];
}
