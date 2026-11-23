using KanbanHub.Domain.Events;

namespace KanbanHub.Domain.Notifications;

/// <summary>What is known about a recipient: where to email them, and the channels they chose per kind.</summary>
public sealed record RecipientProfile(Guid UserId, string? Email, IReadOnlyDictionary<NotificationKind, Channels> Preferences);

/// <summary>A notification ready to store and deliver to one person.</summary>
public sealed record Notification(Guid UserId, Guid BoardId, Guid CardId, PlannedNotification Planned, Channels Channels, string? EmailAddress)
{
    /// <summary>Makes storing it idempotent: the same event notifies the same person once.</summary>
    public required string DedupeKey { get; init; }
}

public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>Sends an email: Azure Communication Services in the cloud, a log line on a development machine.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Storage port for creating notifications.</summary>
public interface INotificationStore
{
    /// <summary>The card and the people around it, or null when the card is gone.</summary>
    Task<CardEventContext?> FindContextAsync(BoardEvent boardEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecipientProfile>> FindProfilesAsync(IReadOnlyList<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Stores the notifications and returns the ones that were not stored before.</summary>
    Task<IReadOnlyList<Notification>> SaveNewAsync(
        IReadOnlyList<Notification> notifications, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>What notifications are about, and the key that makes sending them twice harmless.</summary>
public sealed record NotificationSubject(Guid BoardId, Guid CardId, string DedupeKey);

/// <summary>
/// Turns a board event into notifications: the rules say who, and <see cref="NotificationSender"/> does the rest.
/// </summary>
public sealed class NotificationGenerator(INotificationStore store, NotificationSender sender) : IBoardEventConsumer
{
    public async Task HandleAsync(BoardEvent boardEvent, CancellationToken cancellationToken)
    {
        if (await store.FindContextAsync(boardEvent, cancellationToken) is not { } card) return;

        var subject = new NotificationSubject(boardEvent.BoardId, boardEvent.CardId, $"outbox:{boardEvent.Id}");
        await sender.SendAsync(subject, NotificationPlanner.Plan(boardEvent, card), cancellationToken);
    }
}

/// <summary>
/// Sends planned notifications: each person's choices say how, and storing them first makes a repeat harmless,
/// because only notifications that were not stored before are delivered.
/// </summary>
public sealed class NotificationSender(
    INotificationStore store, IRealtimeGateway realtime, IEmailSender email, TimeProvider clock)
{
    public async Task SendAsync(NotificationSubject subject, IReadOnlyList<PlannedNotification> planned, CancellationToken cancellationToken)
    {
        if (planned.Count == 0) return;

        IReadOnlyList<Notification> addressed = await AddressAsync(subject, planned, cancellationToken);
        foreach (Notification created in await store.SaveNewAsync(addressed, clock.GetUtcNow(), cancellationToken))
            await DeliverAsync(created, cancellationToken);
    }

    /// <summary>Applies each recipient's channel choices and drops notifications nobody wants on any channel.</summary>
    private async Task<IReadOnlyList<Notification>> AddressAsync(
        NotificationSubject subject, IReadOnlyList<PlannedNotification> planned, CancellationToken cancellationToken)
    {
        IReadOnlyList<RecipientProfile> profiles =
            await store.FindProfilesAsync([.. planned.Select(notification => notification.UserId)], cancellationToken);

        return
        [
            .. planned
                .Select(notification => Address(subject, notification, profiles.FirstOrDefault(p => p.UserId == notification.UserId)))
                .Where(notification => notification.Channels.Any)
        ];
    }

    private static Notification Address(NotificationSubject subject, PlannedNotification planned, RecipientProfile? profile)
    {
        Channels? preference = profile?.Preferences.TryGetValue(planned.Kind, out Channels chosen) == true ? chosen : null;
        Channels channels = ChannelPolicy.Resolve(planned.Kind, preference);
        return new Notification(planned.UserId, subject.BoardId, subject.CardId, planned, channels, profile?.Email)
        {
            DedupeKey = subject.DedupeKey
        };
    }

    private async Task DeliverAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (notification.Channels.InApp)
            await realtime.InboxChangedAsync(notification.UserId, cancellationToken);

        if (notification is { Channels.Email: true, EmailAddress: { } address })
            await email.SendAsync(new EmailMessage(address, notification.Planned.Title, notification.Planned.Detail ?? ""), cancellationToken);
    }
}
