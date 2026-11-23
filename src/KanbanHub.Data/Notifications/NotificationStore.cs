using System.Data;
using KanbanHub.Contracts;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;
using Microsoft.Data.SqlClient;

namespace KanbanHub.Data.Notifications;

/// <summary>Stores notifications and what people do with them; reads the inbox as contracts.</summary>
public sealed class NotificationStore(ISqlConnectionFactory database) : INotificationStore, IInboxStore
{
    private const string UsersIn = "(SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@Users))";

    private const string InboxSql = """
        SELECT TOP (100) NotificationId, BoardId, CardId, Kind, Title, Detail, CreatedAtUtc, ReadAtUtc
        FROM kb.[Notification]
        WHERE UserId = @UserId AND ShowInInbox = 1 AND (SnoozedUntilUtc IS NULL OR SnoozedUntilUtc <= @NowUtc)
        ORDER BY CreatedAtUtc DESC, NotificationId DESC;
        """;

    /// <summary>The row the save procedure reads with <c>OPENJSON</c>.</summary>
    private sealed record NotificationRow(
        Guid UserId, Guid BoardId, Guid CardId, string Kind, string Title, string? Detail, string DedupeKey, bool ShowInInbox);

    public Task<CardEventContext?> FindContextAsync(BoardEvent boardEvent, CancellationToken cancellationToken) =>
        CardEventContextQuery.ExecuteAsync(database, boardEvent, cancellationToken);

    public async Task<IReadOnlyList<RecipientProfile>> FindProfilesAsync(IReadOnlyList<Guid> userIds, CancellationToken cancellationToken)
    {
        Dictionary<Guid, string?> emails = (await ListForUsersAsync(
            $"SELECT UserId, Email FROM kb.AppUser WHERE UserId IN {UsersIn};", userIds,
            r => (UserId: r.GetGuid(0), Email: r.StringOrNull(1)), cancellationToken)).ToDictionary(row => row.UserId, row => row.Email);

        ILookup<Guid, (NotificationKind Kind, Channels Channels)> preferences = (await ListPreferenceRowsAsync(userIds, cancellationToken))
            .ToLookup(row => row.UserId, row => (row.Kind, row.Channels));

        return [.. userIds.Select(user => new RecipientProfile(
            user, emails.GetValueOrDefault(user), preferences[user].ToDictionary(p => p.Kind, p => p.Channels)))];
    }

    public async Task<IReadOnlyList<Notification>> SaveNewAsync(
        IReadOnlyList<Notification> notifications, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SaveNotifications");
        command.Parameters.AddJson("@NotificationsJson", notifications.Select(ToRow));
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        List<(Guid UserId, string DedupeKey)> stored = await database.ListAsync(command, r => (r.GetGuid(0), r.GetString(1)), cancellationToken);
        return [.. notifications.Where(notification => stored.Contains((notification.UserId, notification.DedupeKey)))];
    }

    public async Task<int> MarkReadAsync(Guid userId, long? notificationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = UpdateCommand(userId, notificationId, markRead: true, snoozedUntil: null, now);
        return await database.ResultCodeAsync(command, cancellationToken);
    }

    public async Task<bool> SnoozeAsync(Guid userId, long notificationId, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using SqlCommand command = UpdateCommand(userId, notificationId, markRead: false, until, now: until);
        return await database.ResultCodeAsync(command, cancellationToken) > 0;
    }

    public async Task SetCardMuteAsync(Guid userId, Guid cardId, bool isMuted, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetCardMute");
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddGuid("@CardId", cardId);
        command.Parameters.AddValue("@IsMuted", SqlDbType.Bit, isMuted);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task SetPreferenceAsync(Guid userId, NotificationKind kind, Channels channels, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_SetNotificationPreference");
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddValue("@Kind", SqlDbType.VarChar, kind.ToString(), size: 40);
        command.Parameters.AddValue("@InApp", SqlDbType.Bit, channels.InApp);
        command.Parameters.AddValue("@Email", SqlDbType.Bit, channels.Email);

        await database.ExecuteAsync(command, cancellationToken);
    }

    public async Task RememberUserAsync(Guid userId, string displayName, string? email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Procedure("kb.usp_UpsertAppUser");
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddText("@DisplayName", displayName, size: 200);
        command.Parameters.AddText("@Email", email, size: 320);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        await database.ExecuteAsync(command, cancellationToken);
    }

    /// <summary>What the inbox shows: notifications that aren't snoozed, newest first.</summary>
    public async Task<IReadOnlyList<NotificationDto>> ListInboxAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(InboxSql);
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);

        return await database.ListAsync(command, ReadNotification, cancellationToken);
    }

    /// <summary>The channel choices one person stored; kinds they never touched are absent.</summary>
    public async Task<IReadOnlyDictionary<NotificationKind, Channels>> ListPreferencesAsync(Guid userId, CancellationToken cancellationToken) =>
        (await ListPreferenceRowsAsync([userId], cancellationToken)).ToDictionary(row => row.Kind, row => row.Channels);

    private Task<List<(Guid UserId, NotificationKind Kind, Channels Channels)>> ListPreferenceRowsAsync(
        IReadOnlyList<Guid> userIds, CancellationToken cancellationToken) =>
        ListForUsersAsync(
            $"SELECT UserId, Kind, InApp, Email FROM kb.NotificationPreference WHERE UserId IN {UsersIn};", userIds,
            r => (r.GetGuid(0), Enum.Parse<NotificationKind>(r.GetString(1)), new Channels(r.GetBoolean(2), r.GetBoolean(3))),
            cancellationToken);

    private async Task<List<T>> ListForUsersAsync<T>(
        string sql, IReadOnlyList<Guid> userIds, Func<SqlDataReader, T> map, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Sql.Query(sql);
        command.Parameters.AddJson("@Users", userIds);
        return await database.ListAsync(command, map, cancellationToken);
    }

    private static SqlCommand UpdateCommand(Guid userId, long? notificationId, bool markRead, DateTimeOffset? snoozedUntil, DateTimeOffset now)
    {
        SqlCommand command = Sql.Procedure("kb.usp_UpdateNotifications");
        command.Parameters.AddGuid("@UserId", userId);
        command.Parameters.AddValue("@NotificationId", SqlDbType.BigInt, notificationId);
        command.Parameters.AddValue("@MarkRead", SqlDbType.Bit, markRead);
        command.Parameters.AddValue("@SnoozedUntilUtc", SqlDbType.DateTime2, snoozedUntil?.UtcDateTime);
        command.Parameters.AddDateTime2("@NowUtc", now.UtcDateTime);
        return command;
    }

    private static NotificationRow ToRow(Notification notification) => new(
        notification.UserId, notification.BoardId, notification.CardId, notification.Planned.Kind.ToString(),
        notification.Planned.Title, notification.Planned.Detail, notification.DedupeKey, notification.Channels.InApp);

    private static NotificationDto ReadNotification(SqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        BoardId = reader.GetGuid(1),
        CardId = reader.GuidOrNull(2),
        Kind = Enum.Parse<NotificationKind>(reader.GetString(3)),
        Title = reader.GetString(4),
        Detail = reader.StringOrNull(5),
        CreatedAtUtc = reader.UtcDateTime(6),
        IsRead = !reader.IsDBNull(7)
    };
}
