using System.Text.Json;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Notifications;

/// <summary>Loads, in one round trip, what the notification rules need to know about an event's card and its people.</summary>
internal static class CardEventContextQuery
{
    private const string Sql = """
        SELECT c.CardNumber, c.Title, c.CreatedBy, c.ModifiedBy, c.BlockedReason
        FROM kb.Card c
        WHERE c.CardId = @CardId;

        SELECT m.ActorId, m.OverrideReason FROM kb.CardMovement m WHERE m.MovementId = @MovementId;

        SELECT cm.AuthorId, cm.Body FROM kb.CardComment cm WHERE cm.CommentId = @CommentId;

        -- 1 manages the board (Owner or Flow Manager), 2 took part in the conversation, 3 muted the card
        SELECT ra.PrincipalId, Relation = 1
        FROM kb.RoleAssignment ra
        JOIN kb.Board b ON b.BoardId = @BoardId
        WHERE ra.PrincipalType = 1 AND ra.RoleId IN (1, 2)
          AND (ra.BoardId = b.BoardId OR (ra.BoardId IS NULL AND ra.WorkspaceId = b.WorkspaceId))
        UNION ALL
        SELECT DISTINCT cm.AuthorId, 2 FROM kb.CardComment cm WHERE cm.CardId = @CardId
        UNION ALL
        SELECT mu.UserId, 3 FROM kb.CardMute mu WHERE mu.CardId = @CardId;
        """;

    private enum Relation { Manager = 1, Participant = 2, Muted = 3 }

    private sealed record CardRow(int Number, string Title, Guid CreatedBy, Guid ModifiedBy, string? BlockedReason);

    /// <summary>Who caused something and the text that came with it.</summary>
    private sealed record Cause(Guid ActorId, string? Text);

    public static async Task<CardEventContext?> ExecuteAsync(
        ISqlConnectionFactory database, BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await database.OpenAsync(cancellationToken);
        await using SqlCommand command = CreateCommand(connection, boardEvent);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) return null;
        var card = new CardRow(reader.GetInt32(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.StringOrNull(4));

        Cause? move = await NextCauseAsync(reader, cancellationToken);
        Cause? comment = await NextCauseAsync(reader, cancellationToken);
        ILookup<Relation, Guid> people = await NextPeopleAsync(reader, cancellationToken);

        return Assemble(card, move, comment, people);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, BoardEvent boardEvent)
    {
        using JsonDocument payload = JsonDocument.Parse(boardEvent.Payload);
        SqlCommand command = Data.Sql.Query(Sql);
        command.Connection = connection;
        command.Parameters.AddGuid("@CardId", boardEvent.CardId);
        command.Parameters.AddGuid("@BoardId", boardEvent.BoardId);
        command.Parameters.AddValue("@MovementId", System.Data.SqlDbType.BigInt,
            payload.RootElement.TryGetProperty("movementId", out JsonElement movement) ? movement.GetInt64() : null);
        command.Parameters.AddGuid("@CommentId",
            payload.RootElement.TryGetProperty("commentId", out JsonElement comment) ? comment.GetGuid() : null);
        return command;
    }

    private static async Task<Cause?> NextCauseAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        await reader.NextResultAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new Cause(reader.GetGuid(0), reader.StringOrNull(1)) : null;
    }

    private static async Task<ILookup<Relation, Guid>> NextPeopleAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        await reader.NextResultAsync(cancellationToken);
        List<(Relation Relation, Guid UserId)> people = [];
        while (await reader.ReadAsync(cancellationToken)) people.Add(((Relation)reader.GetInt32(1), reader.GetGuid(0)));
        return people.ToLookup(person => person.Relation, person => person.UserId);
    }

    /// <summary>A move or a comment names its own actor and text; any other event is the card's last editor and block reason.</summary>
    private static CardEventContext Assemble(CardRow card, Cause? move, Cause? comment, ILookup<Relation, Guid> people) => new()
    {
        CardNumber = card.Number,
        CardTitle = card.Title,
        CreatedBy = card.CreatedBy,
        ActorId = (comment ?? move)?.ActorId ?? card.ModifiedBy,
        Detail = comment?.Text ?? move?.Text ?? card.BlockedReason,
        IsWipOverride = move?.Text is not null,
        Managers = [.. people[Relation.Manager]],
        Participants = [.. people[Relation.Participant]],
        Muted = [.. people[Relation.Muted]]
    };
}
