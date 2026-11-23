using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.Infrastructure;

internal sealed record CardSnapshot(Location Location, Guid? StepId, Guid? ParkedFromStepId, byte[] RowVersion);

internal sealed record MovementRecord(
    long MovementId, Location FromLocation, Guid? FromStepId, Location ToLocation, Guid? ToStepId,
    MoveSource Source, Guid ActorId, string? OverrideReason, string? Reason, string? ViolationsJson);

internal sealed record ChecklistRecord(string Text, bool IsRequired, bool IsChecked);

internal sealed record FlowRecord(DateTime? FirstStartedAtUtc, DateTime? FinishedAtUtc);

/// <summary>Reads what the procedure left behind, for assertions.</summary>
internal sealed class BoardRecords(ISqlConnectionFactory database)
{
    public Task<CardSnapshot> CardAsync(Guid cardId) =>
        database.QuerySingleAsync(
            "SELECT [Location], StepId, ParkedFromStepId, CAST([RowVersion] AS binary(8)) FROM kb.Card WHERE CardId = @CardId;",
            new { CardId = cardId },
            r => new CardSnapshot((Location)r.GetByte(0), r.GetNullableGuid(1), r.GetNullableGuid(2), r.GetFieldValue<byte[]>(3)));

    /// <summary>Cards in the step's Active and Done sub-columns, as a column header would total them.</summary>
    public async Task<int> ActiveAndDoneAsync(TestStep step) =>
        (await database.QueryAsync(
            "SELECT CardId FROM kb.Card WHERE StepId = @StepId AND [Location] IN (1, 2) AND IsDeleted = 0;",
            new { StepId = step.Id },
            r => r.GetGuid(0))).Count;

    public Task<List<MovementRecord>> MovementsAsync(Guid cardId) =>
        database.QueryAsync("""
            SELECT MovementId, FromLocation, FromStepId, ToLocation, ToStepId, Source, ActorId,
                   OverrideReason, Reason, ViolationsJson
            FROM kb.CardMovement WHERE CardId = @CardId ORDER BY MovementId;
            """,
            new { CardId = cardId },
            r => new MovementRecord(
                r.GetInt64(0), (Location)r.GetByte(1), r.GetNullableGuid(2), (Location)r.GetByte(3), r.GetNullableGuid(4),
                (MoveSource)r.GetByte(5), r.GetGuid(6), r.GetNullableString(7), r.GetNullableString(8), r.GetNullableString(9)));

    public Task<List<string>> OutboxPayloadsAsync(Guid cardId) =>
        database.QueryAsync(
            "SELECT Payload FROM kb.Outbox WHERE AggregateId = @CardId AND EventType = 'card.moved' ORDER BY OutboxId;",
            new { CardId = cardId },
            r => r.GetString(0));

    public Task<List<ChecklistRecord>> ChecklistAsync(Guid cardId, TestStep step) =>
        database.QueryAsync(
            "SELECT [Text], IsRequired, IsChecked FROM kb.CardChecklistItem WHERE CardId = @CardId AND StepId = @StepId ORDER BY SortOrder;",
            new { CardId = cardId, StepId = step.Id },
            r => new ChecklistRecord(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2)));

    public Task<FlowRecord> FlowAsync(Guid cardId) =>
        database.QuerySingleAsync(
            "SELECT FirstStartedAtUtc, FinishedAtUtc FROM kb.vCardFlow WHERE CardId = @CardId;",
            new { CardId = cardId },
            r => new FlowRecord(r.GetNullableDateTime(0), r.GetNullableDateTime(1)));
}
