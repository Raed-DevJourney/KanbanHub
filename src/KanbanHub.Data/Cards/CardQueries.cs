using KanbanHub.Contracts;
using KanbanHub.Domain.GitHub;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Cards;

/// <summary>Read side for single cards. Returns contracts directly: reads carry no domain rules.</summary>
public sealed class CardQueries(ISqlConnectionFactory database)
{
    private const string DetailSql = $"""
        SELECT {CardRow.DetailColumns}
        FROM {CardRow.Source}
        WHERE c.CardId = @CardId AND c.IsDeleted = 0;
        """;

    private const string ChecklistSql = """
        SELECT ChecklistItemId, StepId, [Text], IsRequired, IsChecked
        FROM kb.CardChecklistItem
        WHERE CardId = @CardId
        ORDER BY StepId, SortOrder;
        """;

    private const string PullRequestsSql = """
        SELECT p.PullNumber, p.[State], p.IsDraft, r.FullName
        FROM kb.GitHubPullRequest p
        LEFT JOIN kb.GitHubRepoLink r ON r.RepositoryId = p.RepositoryId
        WHERE p.CardId = @CardId
        ORDER BY p.PullNumber;
        """;

    private const string DuplicatesSql = """
        SELECT o.CardId, o.CardNumber, o.Title, d.SimilarityPercent
        FROM kb.CardDuplicate d
        JOIN kb.Card o ON o.CardId = IIF(d.CardId = @CardId, d.OtherCardId, d.CardId)
        WHERE (d.CardId = @CardId OR d.OtherCardId = @CardId) AND d.[State] = 1
          AND o.IsDeleted = 0 AND o.[Location] <> 5
        ORDER BY d.SimilarityPercent DESC, o.CardNumber;
        """;

    /// <summary>The card with its checklist, pull requests and possible duplicates, or null when there is no such card.</summary>
    public async Task<CardDetailDto?> FindAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand cardCommand = Sql.Query(DetailSql);
        cardCommand.Parameters.AddGuid("@CardId", cardId);

        if (await database.FirstOrDefaultAsync(cardCommand, CardRow.ReadDetail, cancellationToken) is not { } card) return null;

        return card with
        {
            Checklist = await ReadChecklistAsync(cardId, cancellationToken),
            PullRequests = await ReadPullRequestsAsync(cardId, cancellationToken),
            Duplicates = await ReadDuplicatesAsync(cardId, cancellationToken)
        };
    }

    /// <summary>A suggestion shows on both cards of its pair, whichever of them it was found from.</summary>
    private async Task<List<DuplicateDto>> ReadDuplicatesAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(DuplicatesSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(
            command,
            reader => new DuplicateDto(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetByte(3)),
            cancellationToken);
    }

    private async Task<List<PullRequestDto>> ReadPullRequestsAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(PullRequestsSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(
            command,
            reader => new PullRequestDto(reader.GetInt32(0), (PullRequestState)reader.GetByte(1), reader.GetBoolean(2), reader.StringOrNull(3)),
            cancellationToken);
    }

    private async Task<List<ChecklistItemDto>> ReadChecklistAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(ChecklistSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(
            command,
            reader => new ChecklistItemDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)),
            cancellationToken);
    }
}
