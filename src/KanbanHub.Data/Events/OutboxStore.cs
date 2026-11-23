using KanbanHub.Domain.Events;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Events;

/// <summary>Reads unpublished events from <c>kb.Outbox</c> and marks them published.</summary>
public sealed class OutboxStore(ISqlConnectionFactory database) : IOutboxStore
{
    private const string PendingSql = """
        SELECT TOP (@Max) OutboxId, BoardId, AggregateId, EventType, Payload
        FROM kb.Outbox
        WHERE DispatchedAtUtc IS NULL AND CreatedAtUtc <= @CreatedBefore
        ORDER BY OutboxId;
        """;

    public async Task<IReadOnlyList<BoardEvent>> ListPendingAsync(int max, DateTimeOffset createdBefore, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(PendingSql);
        command.Parameters.AddValue("@Max", System.Data.SqlDbType.Int, max);
        command.Parameters.AddDateTime2("@CreatedBefore", createdBefore.UtcDateTime);

        return await database.ListAsync(
            command, r => new BoardEvent(r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4)), cancellationToken);
    }

    public async Task MarkDispatchedAsync(IReadOnlyList<long> eventIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_MarkOutboxDispatched");
        command.Parameters.AddJson("@IdsJson", eventIds);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }
}
