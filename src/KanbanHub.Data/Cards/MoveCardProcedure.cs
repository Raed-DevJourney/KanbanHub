using System.Data;
using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Binds a <see cref="MoveRequest"/> to the parameters of <c>kb.usp_MoveCard</c>.</summary>
internal static class MoveCardProcedure
{
    private const string Name = "kb.usp_MoveCard";
    private const int TimeoutSeconds = 10;

    public static SqlCommand CreateCommand(SqlConnection connection, MoveRequest request)
    {
        var command = new SqlCommand(Name, connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = TimeoutSeconds
        };

        SqlParameterCollection parameters = command.Parameters;
        parameters.AddGuid("@CardId", request.CardId);
        parameters.AddBinary("@ExpectedRowVersion", request.ExpectedRowVersion, size: 8);
        parameters.AddGuid("@ToStepId", request.To.StepId);
        parameters.AddTinyInt("@ToLocation", (byte)request.To.Location);
        parameters.AddDecimal("@ToRank", request.ToRank, precision: 19, scale: 9);
        parameters.AddGuid("@ActorId", request.ActorId);
        parameters.AddText("@OverrideReason", request.OverrideReason, size: 500);
        parameters.AddText("@Reason", request.Reason, size: 500);
        parameters.AddTinyInt("@Source", (byte)request.Source);
        parameters.AddGuid("@IdempotencyKey", request.IdempotencyKey);
        parameters.AddDateTime2("@NowUtc", request.Now.UtcDateTime);
        return command;
    }
}
