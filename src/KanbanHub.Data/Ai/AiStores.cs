using System.Data;
using System.Text.Json;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Boards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Ai;

/// <summary>A board's AI opt-in and budget (<c>kb.Board</c>) against what it spent (<c>kb.AiUsage</c>).</summary>
public sealed class AiPolicyStore(ISqlConnectionFactory database) : IAiPolicyStore
{
    private const string PolicySql = """
        SELECT b.AiEnabled, b.AiMonthlyTokenBudget,
               Used = COALESCE((SELECT SUM(u.Tokens) FROM kb.AiUsage u WHERE u.BoardId = b.BoardId AND u.UsageMonth = @Month), 0)
        FROM kb.Board b
        WHERE b.BoardId = @BoardId AND b.IsArchived = 0;
        """;

    private const string EnabledSql =
        "SELECT BoardId, StandupTimeLocal, TimeZoneId FROM kb.Board WHERE AiEnabled = 1 AND IsArchived = 0 ORDER BY BoardId;";

    public async Task<AiPolicy?> FindAsync(Guid boardId, DateOnly month, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(PolicySql);
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddValue("@Month", SqlDbType.Date, new DateTime(month.Year, month.Month, 1));

        return await database.FirstOrDefaultAsync(
            command, reader => new AiPolicy(reader.GetBoolean(0), reader.GetInt32(1), reader.GetInt64(2)), cancellationToken);
    }

    public async Task RecordUsageAsync(Guid boardId, AiFeature feature, int tokens, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_RecordAiUsage");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddTinyInt("@Feature", (byte)feature);
        command.Parameters.AddValue("@Tokens", SqlDbType.Int, tokens);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AiBoard>> ListEnabledBoardsAsync(CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(EnabledSql);
        return await database.ListAsync(command, ReadBoard, cancellationToken);
    }

    private static AiBoard ReadBoard(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.IsDBNull(1) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)), reader.GetString(2));
}

/// <summary>Insights in <c>kb.AiInsight</c>. The insight itself is kept as JSON: its shape belongs to the domain.</summary>
public sealed class InsightStore(ISqlConnectionFactory database) : IInsightStore
{
    private const string Select = "SELECT InsightId, BoardId, PayloadJson, CreatedAtUtc FROM kb.AiInsight";

    public async Task ReplaceAsync(
        Guid boardId, AiFeature kind, IReadOnlyList<Insight> insights, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_ReplaceInsights");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddTinyInt("@Kind", (byte)kind);
        command.Parameters.AddJson("@InsightsJson", insights);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredInsight>> ListOpenAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"{Select} WHERE BoardId = @BoardId AND [State] = 1 ORDER BY Kind, InsightId;");
        command.Parameters.AddGuid("@BoardId", boardId);

        return await database.ListAsync(command, Read, cancellationToken);
    }

    public async Task<StoredInsight?> FindOpenAsync(Guid boardId, long insightId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query($"{Select} WHERE InsightId = @InsightId AND BoardId = @BoardId AND [State] = 1;");
        command.Parameters.AddBigInt("@InsightId", insightId);
        command.Parameters.AddGuid("@BoardId", boardId);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    public async Task ResolveAsync(long insightId, InsightResolution resolution, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_ResolveInsight");
        command.Parameters.AddBigInt("@InsightId", insightId);
        command.Parameters.AddTinyInt("@State", (byte)resolution);
        command.Parameters.AddStamp(stamp);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private static StoredInsight Read(SqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetGuid(1),
        JsonSerializer.Deserialize<Insight>(reader.GetString(2), JsonSerializerOptions.Web)!,
        new DateTimeOffset(reader.UtcDateTime(3)));
}
