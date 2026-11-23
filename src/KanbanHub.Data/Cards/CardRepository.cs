using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Card storage over ADO.NET. Moves go through <c>kb.usp_MoveCard</c>, the one place WIP is enforced.</summary>
public sealed class CardRepository(ISqlConnectionFactory database) : ICardRepository
{
    public async Task<CardOnBoard> GetOnBoardAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await database.OpenAsync(cancellationToken);
        return await CardOnBoardQuery.ExecuteAsync(connection, cardId, cancellationToken);
    }

    public Task<Moved?> FindAppliedMoveAsync(Guid cardId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        AppliedMoveQuery.ExecuteAsync(database, cardId, idempotencyKey, cancellationToken);

    public async Task<MoveOutcome> MoveAsync(MoveRequest request, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await database.OpenAsync(cancellationToken);
        MoveOutcome outcome = await ExecuteMoveAsync(connection, request, cancellationToken);

        // A full step: point the caller at cards they can help finish instead.
        return outcome is WipLimitReached full && request.To.StepId is { } stepId
            ? full with { SwarmCandidates = await SwarmCandidatesQuery.ExecuteAsync(connection, stepId, cancellationToken) }
            : outcome;
    }

    private static async Task<MoveOutcome> ExecuteMoveAsync(
        SqlConnection connection, MoveRequest request, CancellationToken cancellationToken)
    {
        await using SqlCommand command = MoveCardProcedure.CreateCommand(connection, request);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await MoveOutcomeReader.ReadAsync(reader, request.CardId, cancellationToken);
    }
}
