using System.Data;
using KanbanHub.Contracts;
using KanbanHub.Data.Boards;
using KanbanHub.Data.Cards;
using KanbanHub.Domain.GitHub;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.GitHub;

/// <summary>The sync ledger in <c>kb.SyncLedger</c>: what was last synced per card and field.</summary>
public sealed class SyncLedgerStore(ISqlConnectionFactory database) : ISyncLedger
{
    private const string LastSql = """
        SELECT TOP (1) Direction, Field, ValueHash, SourceUpdated
        FROM kb.SyncLedger
        WHERE CardId = @CardId AND Field = @Field
        ORDER BY OccurredAtUtc DESC, LedgerId DESC;
        """;

    public async Task<LedgerEntry?> FindLastAsync(Guid cardId, string field, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(LastSql);
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddValue("@Field", SqlDbType.VarChar, field, size: 40);

        return await database.FirstOrDefaultAsync(command, Read, cancellationToken);
    }

    public async Task RecordAsync(Guid cardId, LedgerEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_RecordSync");
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddTinyInt("@Direction", (byte)entry.Direction);
        command.Parameters.AddValue("@Field", SqlDbType.VarChar, entry.Field, size: 40);
        command.Parameters.AddBinary("@ValueHash", entry.ValueHash, size: 32);
        command.Parameters.AddDateTime2("@SourceUpdated", entry.SourceUpdated.UtcDateTime);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private static LedgerEntry Read(SqlDataReader reader) => new(
        (SyncDirection)reader.GetByte(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2),
        new DateTimeOffset(reader.UtcDateTime(3)));
}

/// <summary>Cards as the sync reads them: the card and its board from the read side, plus what only the sync asks for.</summary>
public sealed class SyncedCards(ISqlConnectionFactory database, CardQueries cards, BoardQueries boards) : ISyncedCards
{
    private const string ChecklistSql = """
        SELECT i.[Text], i.IsRequired, i.IsChecked
        FROM kb.CardChecklistItem i
        JOIN kb.Card c ON c.CardId = i.CardId
        WHERE i.CardId = @CardId AND i.StepId = c.StepId
        ORDER BY i.SortOrder;
        """;

    private const string UnsentCommentsSql = """
        SELECT cm.CommentId, COALESCE(u.DisplayName, N'Someone'), cm.Body
        FROM kb.CardComment cm
        JOIN kb.GitHubIssueLink l ON l.CardId = cm.CardId
        LEFT JOIN kb.AppUser u ON u.UserId = cm.AuthorId
        WHERE cm.CardId = @CardId AND cm.Origin = 1 AND cm.GitHubCommentId IS NULL AND cm.CreatedAtUtc >= l.LinkedAtUtc
        ORDER BY cm.CreatedAtUtc, cm.CommentId;
        """;

    public async Task<SyncedCard?> FindAsync(Guid cardId, CancellationToken cancellationToken)
    {
        if (await cards.FindAsync(cardId, cancellationToken) is not { } card) return null;
        if (await boards.FindSnapshotAsync(card.BoardId, cancellationToken) is not { } board) return null;

        return new SyncedCard
        {
            CardId = cardId,
            BoardId = card.BoardId,
            Number = card.Card.Number,
            RowVersion = Convert.FromBase64String(card.Card.ETag),
            Content = card.ToContent(),
            Location = card.Card.Location,
            StepId = card.Card.StepId,
            IsBlocked = card.Card.IsBlocked,
            BlockedReason = card.Card.BlockedReason,
            Steps = [.. board.Steps.Select(ToStep)]
        };
    }

    public async Task<IReadOnlyList<ChecklistLine>> ListChecklistAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(ChecklistSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(
            command, reader => new ChecklistLine(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)), cancellationToken);
    }

    public async Task<IReadOnlyList<OutgoingComment>> ListUnsentCommentsAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(UnsentCommentsSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(
            command, reader => new OutgoingComment(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)), cancellationToken);
    }

    public async Task MarkCommentSentAsync(Guid commentId, long gitHubCommentId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_MarkCommentSent");
        command.Parameters.AddGuid("@CommentId", commentId);
        command.Parameters.AddBigInt("@GitHubCommentId", gitHubCommentId);

        await database.ExecuteAsync(command, cancellationToken);
    }

    private static SyncedStep ToStep(StepDto step) => new(step.Id, step.Name, step.Order)
    {
        HasBlockedColumn = step.HasTrack,
        IsFinal = step.IsFinal,
        IsAgentEligible = step.IsAgentEligible,
        WipCount = step.WipCount,
        WipLimit = step.WipLimit
    };
}

/// <summary>Pull requests that close linked issues, in <c>kb.GitHubPullRequest</c>.</summary>
public sealed class PullRequestStore(ISqlConnectionFactory database) : IPullRequestStore
{
    private const string ListSql = """
        SELECT RepositoryId, PullNumber, CardId, HeadSha, CheckRunId, [State], IsDraft
        FROM kb.GitHubPullRequest
        WHERE CardId = @CardId
        ORDER BY PullNumber;
        """;

    public async Task SaveAsync(PullRequestLink pullRequest, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SavePullRequest");
        command.Parameters.AddBigInt("@RepositoryId", pullRequest.RepositoryId);
        command.Parameters.AddValue("@PullNumber", SqlDbType.Int, pullRequest.Number);
        command.Parameters.AddGuid("@CardId", pullRequest.CardId);
        command.Parameters.AddValue("@HeadSha", SqlDbType.VarChar, pullRequest.HeadSha, size: 64);
        command.Parameters.AddBigInt("@CheckRunId", pullRequest.CheckRunId);
        command.Parameters.AddTinyInt("@State", (byte)pullRequest.State);
        command.Parameters.AddValue("@IsDraft", SqlDbType.Bit, pullRequest.IsDraft);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<PullRequestLink>> ListAsync(Guid cardId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(ListSql);
        command.Parameters.AddGuid("@CardId", cardId);

        return await database.ListAsync(command, Read, cancellationToken);
    }

    private static PullRequestLink Read(SqlDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4))
        {
            State = (PullRequestState)reader.GetByte(5),
            IsDraft = reader.GetBoolean(6)
        };
}

/// <summary>Webhook deliveries that were dealt with, in <c>kb.WebhookDelivery</c>.</summary>
public sealed class WebhookDeliveryStore(ISqlConnectionFactory database) : IWebhookDeliveries
{
    public async Task<bool> WasHandledAsync(string deliveryId, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query("SELECT COUNT(*) FROM kb.WebhookDelivery WHERE DeliveryId = @DeliveryId;");
        command.Parameters.AddValue("@DeliveryId", SqlDbType.VarChar, deliveryId, size: 100);

        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }

    public async Task RecordAsync(ParsedDelivery delivery, DeliveryStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_RecordWebhookDelivery");
        command.Parameters.AddValue("@DeliveryId", SqlDbType.VarChar, delivery.DeliveryId, size: 100);
        command.Parameters.AddValue("@EventName", SqlDbType.VarChar, delivery.EventName, size: 60);
        command.Parameters.AddTinyInt("@Status", (byte)status);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }
}
