using System.Security.Claims;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Security;

namespace KanbanHub.Domain.Notifications;

/// <summary>Storage port for what a person does with their own notifications.</summary>
public interface IInboxStore
{
    /// <summary>Marks one notification read, or all of them when <paramref name="notificationId"/> is null. Returns how many changed.</summary>
    Task<int> MarkReadAsync(Guid userId, long? notificationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Hides a notification until the given time. Returns false when it isn't this person's.</summary>
    Task<bool> SnoozeAsync(Guid userId, long notificationId, DateTimeOffset until, CancellationToken cancellationToken);

    Task SetCardMuteAsync(Guid userId, Guid cardId, bool isMuted, CancellationToken cancellationToken);

    Task SetPreferenceAsync(Guid userId, NotificationKind kind, Channels channels, CancellationToken cancellationToken);

    Task RememberUserAsync(Guid userId, string displayName, string? email, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>A person's inbox: read, snooze, mute a card, choose channels (specification section 11.3).</summary>
public sealed class Inbox(IInboxStore store, ICardStore cards, IBoardAuthorizer authorizer, TimeProvider clock)
{
    public const int MaxSnoozeHours = 24 * 7;

    private static readonly NotFound NoSuchNotification = new("notification");

    public async Task<Outcome<Saved>> MarkReadAsync(long notificationId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        int changed = await store.MarkReadAsync(user.GetObjectId(), notificationId, clock.GetUtcNow(), cancellationToken);
        return changed > 0 ? Saved.Instance : NoSuchNotification;
    }

    public async Task<Outcome<Saved>> MarkAllReadAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await store.MarkReadAsync(user.GetObjectId(), notificationId: null, clock.GetUtcNow(), cancellationToken);
        return Saved.Instance;
    }

    public async Task<Outcome<Saved>> SnoozeAsync(long notificationId, int hours, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (hours is < 1 or > MaxSnoozeHours) return new Invalid($"A notification can be snoozed for 1 to {MaxSnoozeHours} hours.");

        DateTimeOffset until = clock.GetUtcNow().AddHours(hours);
        return await store.SnoozeAsync(user.GetObjectId(), notificationId, until, cancellationToken) ? Saved.Instance : NoSuchNotification;
    }

    /// <summary>Muting only changes what the caller hears, so being able to read the card is enough.</summary>
    public async Task<Outcome<Saved>> SetCardMuteAsync(Guid cardId, bool isMuted, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await cards.FindFactsAsync(cardId, cancellationToken) is not { } card) return new NotFound("card");
        if (await authorizer.DenyUnlessAsync(user, card.BoardId, Permission.BoardRead, cancellationToken) is { } denied) return denied;

        await store.SetCardMuteAsync(user.GetObjectId(), cardId, isMuted, cancellationToken);
        return Saved.Instance;
    }

    public async Task<Outcome<Saved>> SetPreferenceAsync(
        NotificationKind kind, Channels channels, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind)) return new Invalid("Unknown kind of notification.");
        if (!channels.InApp && !ChannelPolicy.CanMute(kind)) return new Invalid($"{kind} notifications can't be turned off in the app.");

        await store.SetPreferenceAsync(user.GetObjectId(), kind, channels, cancellationToken);
        return Saved.Instance;
    }

    /// <summary>Remembers the caller's name and email from their token, so others can be told who did what.</summary>
    public Task RememberAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        store.RememberUserAsync(
            user.GetObjectId(), user.Identity?.Name ?? "Unknown",
            user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value,
            clock.GetUtcNow(), cancellationToken);
}
