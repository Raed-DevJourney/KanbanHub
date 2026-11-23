using KanbanHub.Data;
using KanbanHub.Domain.Events;

namespace KanbanHub.TestSupport;

public static class OutboxEvents
{
    /// <summary>The events a card left in the outbox, oldest first, whether or not they were published yet.</summary>
    public static Task<List<BoardEvent>> EventsOfCardAsync(this ISqlConnectionFactory database, Guid cardId) =>
        database.QueryAsync(
            "SELECT OutboxId, BoardId, AggregateId, EventType, Payload FROM kb.Outbox WHERE AggregateId = @CardId ORDER BY OutboxId;",
            new { CardId = cardId },
            row => new BoardEvent(row.GetInt64(0), row.GetGuid(1), row.GetGuid(2), row.GetString(3), row.GetString(4)));
}
