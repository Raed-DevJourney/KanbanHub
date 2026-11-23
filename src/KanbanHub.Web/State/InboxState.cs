using KanbanHub.Contracts;
using KanbanHub.Domain.Notifications;
using KanbanHub.Web.Api;

namespace KanbanHub.Web.State;

/// <summary>The signed-in person's inbox: what it shows, the unread count for the badge, and what they can do with it.</summary>
public sealed class InboxState(KanbanApi api, Notices notices)
{
    public const int AnHour = 1;
    public const int ADay = 24;

    private bool hasLoaded;

    public event Action? Changed;

    public IReadOnlyList<NotificationDto> Notifications { get; private set; } = [];

    public int UnreadCount { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if ((await api.GetInboxAsync(cancellationToken)).Value is InboxDto inbox)
        {
            AnnounceArrivals(inbox.UnreadCount);
            Notifications = inbox.Notifications;
            UnreadCount = inbox.UnreadCount;
            hasLoaded = true;
        }

        Changed?.Invoke();
    }

    public Task MarkReadAsync(NotificationDto notification, CancellationToken cancellationToken) =>
        ApplyAsync(api.MarkNotificationReadAsync(notification.Id, cancellationToken), cancellationToken);

    public Task MarkAllReadAsync(CancellationToken cancellationToken) =>
        ApplyAsync(api.MarkAllNotificationsReadAsync(cancellationToken), cancellationToken);

    /// <summary>Hides the notification until the hours have passed.</summary>
    public Task SnoozeAsync(NotificationDto notification, int hours, CancellationToken cancellationToken) =>
        ApplyAsync(api.SnoozeNotificationAsync(notification.Id, hours, cancellationToken), cancellationToken);

    /// <summary>Stops notifications about the card this one is about, and files this one as read.</summary>
    public async Task MuteCardAsync(NotificationDto notification, CancellationToken cancellationToken)
    {
        if (notification.CardId is not { } cardId) return;

        ApiResult<Done> muted = await api.SetCardMuteAsync(cardId, isMuted: true, cancellationToken);
        if (muted.Value is ApiProblem problem)
        {
            notices.ShowProblem(problem);
            return;
        }

        notices.Show(NoticeKind.Success, "Muted. You won't be notified about this card again.");
        await MarkReadAsync(notification, cancellationToken);
    }

    /// <summary>The badge is silent for a screen reader, so new mail is also announced.</summary>
    private void AnnounceArrivals(int unreadNow)
    {
        if (hasLoaded && unreadNow > UnreadCount)
            notices.Announce($"New notification. {unreadNow} unread in your inbox.");
    }

    private async Task ApplyAsync(Task<ApiResult<Done>> change, CancellationToken cancellationToken)
    {
        if ((await change).Value is ApiProblem problem) notices.ShowProblem(problem);

        await LoadAsync(cancellationToken);
    }
}

public static class NotificationLabels
{
    /// <summary>The kind of notification in the words people see.</summary>
    public static string Of(NotificationKind kind) => kind switch
    {
        NotificationKind.CardBlocked => "A card is blocked",
        NotificationKind.CardUnblocked => "A card is unblocked",
        NotificationKind.WipOverrideUsed => "A WIP limit was overridden",
        NotificationKind.CommentAdded => "A new comment",
        NotificationKind.CardAging => "A card is taking longer than most",
        _ => kind.ToString()
    };
}
