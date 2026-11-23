using System.Data;
using KanbanHub.Domain;
using KanbanHub.Domain.Boards;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Boards;

/// <summary>Stores a board's preferences, workflow and palette through the write procedures.</summary>
public sealed class BoardSetupStore(ISqlConnectionFactory database) : IBoardSetupStore
{
    private const int StepHoldsCards = 7;

    private const string PreferencesSql = """
        SELECT [Name], ReadyListCap, ParkingReviewWeeks, ArchiveAfterDays, StandupTimeLocal, TimeZoneId, AgentConcurrencyLimit,
               AiEnabled, AiMonthlyTokenBudget
        FROM kb.Board
        WHERE BoardId = @BoardId AND IsArchived = 0;
        """;

    public async Task<BoardPreferences?> FindPreferencesAsync(Guid boardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(PreferencesSql);
        command.Parameters.AddGuid("@BoardId", boardId);

        return await database.FirstOrDefaultAsync(command, ReadPreferences, cancellationToken);
    }

    public async Task SavePreferencesAsync(
        Guid boardId, BoardPreferences preferences, bool archived, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_UpdateBoard");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddText("@Name", preferences.Name.Trim(), size: 200);
        command.Parameters.AddValue("@ReadyListCap", SqlDbType.SmallInt, preferences.ReadyListCap);
        command.Parameters.AddTinyInt("@ParkingReviewWeeks", preferences.ParkingReviewWeeks);
        command.Parameters.AddValue("@ArchiveAfterDays", SqlDbType.SmallInt, preferences.ArchiveAfterDays);
        command.Parameters.AddValue("@StandupTimeLocal", SqlDbType.Time, preferences.StandupTimeLocal?.ToTimeSpan());
        command.Parameters.AddValue("@TimeZoneId", SqlDbType.VarChar, preferences.TimeZoneId, size: 64);
        command.Parameters.AddTinyInt("@AgentLimit", preferences.AgentConcurrencyLimit);
        command.Parameters.AddValue("@AiEnabled", SqlDbType.Bit, preferences.AiEnabled);
        command.Parameters.AddValue("@AiTokenBudget", SqlDbType.Int, preferences.AiMonthlyTokenBudget);
        command.Parameters.AddValue("@IsArchived", SqlDbType.Bit, archived);
        command.Parameters.AddStamp(stamp);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<Outcome<Saved>> SetWorkflowAsync(
        Guid boardId, IReadOnlyList<StepDraft> steps, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetWorkflow");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddJson("@StepsJson", steps);
        command.Parameters.AddStamp(stamp);

        return await database.SingleAsync(command, ReadWorkflowOutcome, cancellationToken);
    }

    public async Task SetPaletteAsync(
        Guid boardId, IReadOnlyList<PaletteColor> colors, Stamp stamp, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetPalette");
        command.Parameters.AddGuid("@BoardId", boardId);
        command.Parameters.AddJson("@ColorsJson", colors);
        command.Parameters.AddStamp(stamp);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private static BoardPreferences ReadPreferences(SqlDataReader reader) => new()
    {
        Name = reader.GetString(0),
        ReadyListCap = reader.Int16OrNull(1),
        ParkingReviewWeeks = reader.GetByte(2),
        ArchiveAfterDays = reader.GetInt16(3),
        StandupTimeLocal = reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
        TimeZoneId = reader.GetString(5),
        AgentConcurrencyLimit = reader.GetByte(6),
        AiEnabled = reader.GetBoolean(7),
        AiMonthlyTokenBudget = reader.GetInt32(8)
    };

    private static Outcome<Saved> ReadWorkflowOutcome(SqlDataReader reader) => reader.GetInt32(0) switch
    {
        ResultCodes.Ok => Saved.Instance,
        ResultCodes.NotFound => new NotFound("board"),
        StepHoldsCards => new Invalid($"{reader.GetString(1)} still holds cards. Move them out before removing the step."),
        _ => new NotFound("step")
    };
}
