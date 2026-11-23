using KanbanHub.Domain.Flow;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Loads a card's position together with its board's workflow, in one round trip.</summary>
internal static class CardOnBoardQuery
{
    private const string Sql = """
        SELECT c.BoardId, c.[Location], c.StepId, c.ParkedFromStepId
        FROM kb.Card c
        WHERE c.CardId = @CardId AND c.IsDeleted = 0;

        SELECT s.StepId, s.[Name], s.HasTrack, s.IsFinal, s.CountsParentsOnly
        FROM kb.BoardStep s
        JOIN kb.Card c ON c.BoardId = s.BoardId
        WHERE c.CardId = @CardId AND c.IsDeleted = 0 AND s.IsRetired = 0 AND s.SwimlaneId IS NULL
        ORDER BY s.SortOrder;
        """;

    private sealed record CardRow(Guid BoardId, Location Location, Guid? StepId, Guid? ParkedFromStepId);

    /// <exception cref="CardNotFoundException">No such card.</exception>
    public static async Task<CardOnBoard> ExecuteAsync(SqlConnection connection, Guid cardId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddGuid("@CardId", cardId);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) throw new CardNotFoundException(cardId);
        CardRow card = ReadCard(reader);

        await reader.NextResultAsync(cancellationToken);
        var workflow = new Workflow(await ReadStepsAsync(reader, cancellationToken));

        CardPosition position = workflow.PositionOf(card.Location, card.StepId, card.ParkedFromStepId);
        return new CardOnBoard(cardId, card.BoardId, position, workflow);
    }

    private static CardRow ReadCard(SqlDataReader reader) => new(
        BoardId: reader.GetGuid(0),
        Location: (Location)reader.GetByte(1),
        StepId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
        ParkedFromStepId: reader.IsDBNull(3) ? null : reader.GetGuid(3));

    private static async Task<List<WorkflowStep>> ReadStepsAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        List<WorkflowStep> steps = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(new WorkflowStep(reader.GetGuid(0), reader.GetString(1))
            {
                HasTrack = reader.GetBoolean(2),
                IsFinal = reader.GetBoolean(3),
                CountsParentsOnly = reader.GetBoolean(4)
            });
        }

        return steps;
    }
}
