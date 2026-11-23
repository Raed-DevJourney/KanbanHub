using System.Data;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Cards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Ai;

/// <summary>
/// The aging rule in SQL (section 11.1, feature 4): a card's age in flow against the 85th percentile of the cycle
/// time of the cards of its type that finished on its board in the window, from <c>kb.vCardFlow</c>.
/// </summary>
public sealed class AgingStore(ISqlConnectionFactory database) : IAgingStore
{
    private const string BreachesSql = """
        WITH finished AS (
            SELECT f.BoardId, f.CardType, CycleDays = DATEDIFF(HOUR, f.FirstStartedAtUtc, f.FinishedAtUtc) / 24.0
            FROM kb.vCardFlow f
            WHERE f.IsContainer = 0 AND f.FirstStartedAtUtc IS NOT NULL
              AND f.FinishedAtUtc > DATEADD(DAY, -@WindowDays, @NowUtc)
        ),
        expected AS (
            SELECT DISTINCT BoardId, CardType,
                   ExpectedDays = PERCENTILE_CONT(@Percentile) WITHIN GROUP (ORDER BY CycleDays) OVER (PARTITION BY BoardId, CardType),
                   Finished = COUNT(*) OVER (PARTITION BY BoardId, CardType)
            FROM finished
        )
        SELECT c.CardId, c.BoardId, c.CardNumber, c.Title, c.CardType, s.[Name], c.IsBlocked,
               AgeDays = CAST(DATEDIFF(HOUR, f.FirstStartedAtUtc, @NowUtc) / 24.0 AS float), e.ExpectedDays, e.Finished
        FROM kb.Card c
        JOIN kb.Board b ON b.BoardId = c.BoardId AND b.IsArchived = 0
        JOIN kb.BoardStep s ON s.StepId = c.StepId
        JOIN kb.vCardFlow f ON f.CardId = c.CardId
        JOIN expected e ON e.BoardId = c.BoardId AND e.CardType = c.CardType
        WHERE c.IsDeleted = 0 AND c.IsContainer = 0
          AND c.[Location] IN (1, 2, 3) AND NOT (c.[Location] = 2 AND s.IsFinal = 1)
          AND e.Finished >= @MinFinished
          AND DATEDIFF(HOUR, f.FirstStartedAtUtc, @NowUtc) / 24.0 > e.ExpectedDays
          AND NOT EXISTS (SELECT 1 FROM kb.CardAgingAlert a WHERE a.CardId = c.CardId)
        ORDER BY c.BoardId, c.CardNumber;
        """;

    public async Task<IReadOnlyList<AgingCard>> ListNewBreachesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(BreachesSql);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);
        command.Parameters.AddValue("@WindowDays", SqlDbType.Int, AgingRule.WindowDays);
        command.Parameters.AddValue("@MinFinished", SqlDbType.Int, AgingRule.MinFinishedCards);
        command.Parameters.AddValue("@Percentile", SqlDbType.Float, AgingRule.Percentile / 100.0);

        return await database.ListAsync(command, Read, cancellationToken);
    }

    public async Task MarkAlertedAsync(AgingCard card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_MarkAgingAlerted");
        command.Parameters.AddGuid("@CardId", card.CardId);
        command.Parameters.AddGuid("@BoardId", card.BoardId);
        command.Parameters.AddDecimal("@AgeDays", card.AgeDays, precision: 9, scale: 1);
        command.Parameters.AddDecimal("@ExpectedDays", card.ExpectedDays, precision: 9, scale: 1);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    /// <summary>Days are kept to one decimal: what the alert says is then exactly what it was decided on.</summary>
    private static AgingCard Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), ((CardType)reader.GetByte(4)).ToString(), reader.GetString(5))
    {
        IsBlocked = reader.GetBoolean(6),
        AgeDays = ToDays(reader.GetDouble(7)),
        ExpectedDays = ToDays(reader.GetDouble(8)),
        FinishedCards = reader.GetInt32(9)
    };

    private static decimal ToDays(double days) => Math.Round((decimal)days, 1, MidpointRounding.AwayFromZero);
}
