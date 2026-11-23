using System.Data;
using KanbanHub.Data.Boards;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Planning;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Ai;

/// <summary>
/// The facts the AI features are grounded in, counted here in SQL (section 11.2): a model is never handed raw
/// history to count for itself, only these numbers and the few cards they are about.
/// </summary>
public sealed class FlowFactQueries(ISqlConnectionFactory database, InsightQueries insights) : IFlowFacts
{
    /// <summary>A card counts as aging once it has sat in an Active column this many days.</summary>
    public const int AgingDays = 3;

    private const int MaxCardsPerList = 20;
    private const int Finished = 1, Blocked = 2, Aging = 3, Waiting = 4;

    private const string StepsSql = """
        SELECT s.StepId, s.[Name], s.IsFinal, w.MaxItems,
               ActiveCount  = (SELECT COUNT(*) FROM kb.Card c WHERE c.StepId = s.StepId AND c.[Location] = 1 AND c.IsDeleted = 0),
               DoneCount    = (SELECT COUNT(*) FROM kb.Card c WHERE c.StepId = s.StepId AND c.[Location] = 2 AND c.IsDeleted = 0),
               BlockedCount = (SELECT COUNT(*) FROM kb.Card c WHERE c.StepId = s.StepId AND c.[Location] = 3 AND c.IsDeleted = 0),
               OldestDoneDays = COALESCE((SELECT MAX(DATEDIFF(DAY, c.EnteredLocationAtUtc, @NowUtc)) FROM kb.Card c
                                          WHERE c.StepId = s.StepId AND c.[Location] = 2 AND c.IsDeleted = 0), 0),
               Finished28 = (SELECT COUNT(*) FROM kb.CardMovement m
                             WHERE m.ToStepId = s.StepId AND m.ToLocation = 2 AND m.MovedAtUtc > DATEADD(DAY, -28, @NowUtc)),
               Overrides28 = (SELECT COUNT(*) FROM kb.CardMovement m
                              WHERE m.ToStepId = s.StepId AND m.OverrideReason IS NOT NULL AND m.MovedAtUtc > DATEADD(DAY, -28, @NowUtc))
        FROM kb.BoardStep s
        JOIN kb.Board b ON b.BoardId = s.BoardId
        LEFT JOIN kb.WipLimit w ON w.StepId = s.StepId AND w.Scope = 1 AND w.SwimlaneId IS NULL
        WHERE s.BoardId = @BoardId AND b.IsArchived = 0 AND s.IsRetired = 0 AND s.SwimlaneId IS NULL
        ORDER BY s.SortOrder;
        """;

    // Each card in flow belongs to one list: blocked, finished in the last day, waiting in a Done column, or aging in Active.
    private const string CardsSql = """
        WITH listed AS (
            SELECT Kind = CASE
                       WHEN c.IsBlocked = 1 THEN 2
                       WHEN c.[Location] = 2 AND s.IsFinal = 1 THEN 1
                       WHEN c.[Location] = 2 THEN 4
                       ELSE 3 END,
                   c.CardNumber, c.Title, StepName = s.[Name], c.BlockedReason, c.EnteredLocationAtUtc,
                   Since = IIF(c.IsBlocked = 1, COALESCE(c.BlockedAtUtc, c.EnteredLocationAtUtc), c.EnteredLocationAtUtc)
            FROM kb.Card c
            JOIN kb.BoardStep s ON s.StepId = c.StepId
            WHERE c.BoardId = @BoardId AND c.IsDeleted = 0 AND c.[Location] IN (1, 2, 3)
        )
        SELECT Kind, CardNumber, Title, StepName, Days, BlockedReason
        FROM (
            SELECT *, Days = DATEDIFF(DAY, Since, @NowUtc), Position = ROW_NUMBER() OVER (PARTITION BY Kind ORDER BY Since, CardNumber)
            FROM listed
            WHERE Kind IN (2, 4)
               OR (Kind = 1 AND EnteredLocationAtUtc > DATEADD(HOUR, -24, @NowUtc))
               OR (Kind = 3 AND DATEDIFF(DAY, Since, @NowUtc) >= @AgingDays)
        ) ranked
        WHERE Position <= @MaxPerList
        ORDER BY Kind, Position;
        """;

    private const string LimitsSql = """
        SELECT w.StepId, w.MaxItems, w.[Policy]
        FROM kb.WipLimit w
        JOIN kb.BoardStep s ON s.StepId = w.StepId
        WHERE w.BoardId = @BoardId AND w.Scope = 1 AND w.SwimlaneId IS NULL AND s.IsRetired = 0
        ORDER BY s.SortOrder;
        """;

    private sealed record ListedCard(int Kind, FlowCard Card);

    public async Task<FlowSnapshot?> FindSnapshotAsync(Guid boardId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<StepFlow> steps = await ListStepsAsync(boardId, now, cancellationToken);
        if (steps.Count == 0) return null;

        List<ListedCard> cards = await ListCardsAsync(boardId, now, cancellationToken);
        return new FlowSnapshot
        {
            Steps = [.. steps.Select((step, index) => step with { Order = index + 1 })],
            Finished = Of(cards, Finished),
            Blocked = Of(cards, Blocked),
            Aging = Of(cards, Aging),
            Waiting = Of(cards, Waiting)
        };
    }

    public async Task<IReadOnlyList<StepLimit>> ListLimitsAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(LimitsSql);
        command.Parameters.AddGuid("@BoardId", boardId);

        return await database.ListAsync(
            command, reader => new StepLimit(reader.GetGuid(0), reader.GetInt16(1), (WipPolicy)reader.GetByte(2)), cancellationToken);
    }

    public Task<ForecastInputs> GetForecastInputsAsync(Guid boardId, byte? bucket, int days, CancellationToken cancellationToken) =>
        insights.GetForecastInputsAsync(boardId, bucket, days, cancellationToken);

    private async Task<List<StepFlow>> ListStepsAsync(Guid boardId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(StepsSql);
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        return await database.ListAsync(command, ReadStep, cancellationToken);
    }

    private async Task<List<ListedCard>> ListCardsAsync(Guid boardId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(CardsSql);
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);
        command.Parameters.AddValue("@AgingDays", SqlDbType.Int, AgingDays);
        command.Parameters.AddValue("@MaxPerList", SqlDbType.Int, MaxCardsPerList);

        return await database.ListAsync(command, ReadCard, cancellationToken);
    }

    private static IReadOnlyList<FlowCard> Of(List<ListedCard> cards, int kind) =>
        [.. cards.Where(listed => listed.Kind == kind).Select(listed => listed.Card)];

    private static StepFlow ReadStep(SqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), Order: 0)
    {
        IsFinal = reader.GetBoolean(2),
        WipLimit = reader.Int16OrNull(3),
        ActiveCount = reader.GetInt32(4),
        DoneCount = reader.GetInt32(5),
        BlockedCount = reader.GetInt32(6),
        OldestDoneDays = reader.GetInt32(7),
        FinishedLast28Days = reader.GetInt32(8),
        OverridesLast28Days = reader.GetInt32(9)
    };

    private static ListedCard ReadCard(SqlDataReader reader) => new(
        reader.GetInt32(0),
        new FlowCard(reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)) { Reason = reader.StringOrNull(5) });
}
