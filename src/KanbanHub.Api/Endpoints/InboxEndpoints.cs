using KanbanHub.Api.Http;
using KanbanHub.Contracts;
using KanbanHub.Data.Notifications;
using KanbanHub.Domain;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.Api.Endpoints;

/// <summary>The caller's own notifications: read them, mark them read, snooze them, mute a card, choose channels.</summary>
public sealed class InboxEndpoints(Inbox inbox, NotificationStore notifications, TimeProvider clock)
{
    [Function("GetInbox")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/me/notifications")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NotificationDto> shown =
            await notifications.ListInboxAsync(request.HttpContext.User.GetObjectId(), clock.GetUtcNow(), cancellationToken);
        return new OkObjectResult(new InboxDto(shown, shown.Count(notification => !notification.IsRead)));
    }

    [Function("MarkNotificationRead")]
    public async Task<IActionResult> MarkRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/me/notifications/{notificationId:long}/read")] HttpRequest request,
        long notificationId, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await inbox.MarkReadAsync(notificationId, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("MarkAllNotificationsRead")]
    public async Task<IActionResult> MarkAllRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/me/notifications/read-all")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await inbox.MarkAllReadAsync(request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("SnoozeNotification")]
    public async Task<IActionResult> Snooze(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/me/notifications/{notificationId:long}/snooze")] HttpRequest request,
        long notificationId, CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SnoozeRequest>(cancellationToken) is not { } body) return Problems.BodyRequired();

        Outcome<Saved> outcome = await inbox.SnoozeAsync(notificationId, body.Hours, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    [Function("MuteCard")]
    public Task<IActionResult> Mute(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/cards/{cardId:guid}/mute")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        SetMuteAsync(request, cardId, isMuted: true, cancellationToken);

    [Function("UnmuteCard")]
    public Task<IActionResult> Unmute(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v1/cards/{cardId:guid}/mute")] HttpRequest request,
        Guid cardId, CancellationToken cancellationToken) =>
        SetMuteAsync(request, cardId, isMuted: false, cancellationToken);

    /// <summary>Every kind of notification with the caller's channels: their own choice, or the default.</summary>
    [Function("GetNotificationPreferences")]
    public async Task<IActionResult> GetPreferences(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/me/notification-preferences")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<NotificationKind, Channels> chosen =
            await notifications.ListPreferencesAsync(request.HttpContext.User.GetObjectId(), cancellationToken);

        return new OkObjectResult(Enum.GetValues<NotificationKind>().Select(kind => Describe(kind, chosen)).ToList());
    }

    [Function("SetNotificationPreference")]
    public async Task<IActionResult> SetPreference(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/me/notification-preferences")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (await request.ReadBodyAsync<SetNotificationPreferenceRequest>(cancellationToken) is not { } body)
            return Problems.BodyRequired();

        Outcome<Saved> outcome = await inbox.SetPreferenceAsync(
            body.Kind, new Channels(body.InApp, body.Email), request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    private async Task<IActionResult> SetMuteAsync(HttpRequest request, Guid cardId, bool isMuted, CancellationToken cancellationToken)
    {
        Outcome<Saved> outcome = await inbox.SetCardMuteAsync(cardId, isMuted, request.HttpContext.User, cancellationToken);
        return outcome.ToResult(OutcomeResults.NoContent);
    }

    private static NotificationPreferenceDto Describe(NotificationKind kind, IReadOnlyDictionary<NotificationKind, Channels> chosen)
    {
        Channels channels = ChannelPolicy.Resolve(kind, chosen.TryGetValue(kind, out Channels own) ? own : null);
        return new NotificationPreferenceDto(kind, channels.InApp, channels.Email, ChannelPolicy.CanMute(kind));
    }
}
