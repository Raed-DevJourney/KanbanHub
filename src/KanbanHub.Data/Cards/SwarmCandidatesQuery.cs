using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>
/// "Stop starting, start finishing": the cards in a full step that most need help,
/// blocked ones first, then those that have sat there longest.
/// </summary>
internal static class SwarmCandidatesQuery
{
    private const string Sql = """
        SELECT TOP (3) CardId
        FROM kb.Card
        WHERE StepId = @StepId AND [Location] IN (1, 2) AND IsDeleted = 0
        ORDER BY IsBlocked DESC, EnteredLocationAtUtc ASC;
        """;

    public static async Task<IReadOnlyList<Guid>> ExecuteAsync(SqlConnection connection, Guid stepId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddGuid("@StepId", stepId);

        List<Guid> cardIds = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) cardIds.Add(reader.GetGuid(0));
        return cardIds;
    }
}
