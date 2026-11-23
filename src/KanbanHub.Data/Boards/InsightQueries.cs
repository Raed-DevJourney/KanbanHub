using KanbanHub.Contracts;
using KanbanHub.Domain.Planning;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Boards;

/// <summary>Read side for what a board's history tells: the audit trail and the flow numbers.</summary>
public sealed class InsightQueries(ISqlConnectionFactory database, TimeProvider clock)
{
    // Card moves and every other audited change, newest first, as one trail. The caller pages by time, so a page
    // takes every entry of its last instant (WITH TIES): two changes in the same millisecond are never split.
    // Within an instant a move is the newer one: blocking a card records the flag, then the move it causes.
    private const string AuditSql = """
        SELECT OccurredAtUtc, ActorId, EventType, CardId, Field, OldValue, NewValue, Reason
        FROM (
            SELECT TOP (@Take) WITH TIES *
            FROM (
                SELECT a.OccurredAtUtc, a.ActorId, a.EventType, a.CardId, a.Field, a.OldValue, a.NewValue, a.Reason,
                       IsMove = 0, Sequence = a.AuditEventId
                FROM kb.AuditEvent a
                WHERE a.BoardId = @BoardId
                UNION ALL
                SELECT m.MovedAtUtc, m.ActorId, 'card.moved', m.CardId, CAST(NULL AS nvarchar(100)),
                       CONCAT(m.FromLocation, N' ', fs.[Name]), CONCAT(m.ToLocation, N' ', ts.[Name]),
                       COALESCE(m.OverrideReason, m.Reason), 1, m.MovementId
                FROM kb.CardMovement m
                LEFT JOIN kb.BoardStep fs ON fs.StepId = m.FromStepId
                LEFT JOIN kb.BoardStep ts ON ts.StepId = m.ToStepId
                WHERE m.BoardId = @BoardId
            ) trail
            WHERE OccurredAtUtc < @Before
            ORDER BY OccurredAtUtc DESC
        ) page
        ORDER BY OccurredAtUtc DESC, IsMove DESC, Sequence DESC;
        """;

    private const string ForecastSql = """
        SELECT s.PendingEstimated, s.Active, s.Done, e.PendingEstimated, e.Active, e.Done,
               FinishedInWindow = (SELECT COUNT(*) FROM kb.Card c
                                   JOIN kb.vCardFlow f ON f.CardId = c.CardId
                                   WHERE c.BoardId = @BoardId AND c.IsDeleted = 0 AND c.IsContainer = 0 AND c.[Location] <> 5
                                     AND (@Bucket IS NULL OR c.PriorityBucket = @Bucket)
                                     AND f.FinishedAtUtc > @Since AND f.FinishedAtUtc <= @NowUtc)
        FROM kb.tvf_TaskTotals(@BoardId, @Bucket, @Since) s
        CROSS JOIN kb.tvf_TaskTotals(@BoardId, @Bucket, @NowUtc) e;
        """;

    /// <summary>One page of the board's audit trail, older than <paramref name="before"/>: <paramref name="take"/> entries, plus any others of the last one's instant.</summary>
    public async Task<IReadOnlyList<AuditEntryDto>> ListAuditAsync(
        Guid boardId, DateTimeOffset? before, int take, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(AuditSql);
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddDateTime2("@Before", (before ?? DateTimeOffset.MaxValue).UtcDateTime);
        command.Parameters.AddValue("@Take", System.Data.SqlDbType.Int, take);

        return await database.ListAsync(command, ReadAuditEntry, cancellationToken);
    }

    /// <summary>The task counts the book's forecast needs, over the last <paramref name="days"/> days.</summary>
    public async Task<ForecastInputs> GetForecastInputsAsync(Guid boardId, byte? bucket, int days, CancellationToken cancellationToken)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        await using SqlCommand command = Sql.Query(ForecastSql);
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddTinyInt("@Bucket", bucket);
        command.Parameters.AddDateTime2("@Since", now.AddDays(-days));
        command.Parameters.AddDateTime2("@NowUtc", now);

        return await database.SingleAsync(command, reader => ReadForecastInputs(reader, days), cancellationToken);
    }

    private static AuditEntryDto ReadAuditEntry(SqlDataReader reader) => new()
    {
        OccurredAtUtc = reader.UtcDateTime(0),
        ActorId = reader.GetGuid(1),
        EventType = reader.GetString(2),
        CardId = reader.GuidOrNull(3),
        Field = reader.StringOrNull(4),
        OldValue = reader.StringOrNull(5),
        NewValue = reader.StringOrNull(6),
        Reason = reader.StringOrNull(7)
    };

    private static ForecastInputs ReadForecastInputs(SqlDataReader reader, int days) => new(
        Start: new TaskTotals(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)),
        End: new TaskTotals(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)),
        FinishedInWindow: reader.GetInt32(6),
        Days: days);
}
