using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Tests.Events;

namespace KanbanHub.Domain.Tests.Notifications;

/// <summary>From a board event to stored notifications, inbox pushes and email.</summary>
public class NotificationGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid Creator = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly BoardEvent Blocked = new(42, Guid.NewGuid(), Guid.NewGuid(), BoardEvent.CardBlocked, "{}");
    private static readonly Channels Both = new(InApp: true, Email: true);

    private readonly FakeStore store = new()
    {
        Context = new CardEventContext
        {
            CardNumber = 12,
            CardTitle = "Checkout",
            CreatedBy = Creator,
            ActorId = Guid.NewGuid(),
            Detail = "Waiting for legal",
            Managers = [Manager]
        }
    };

    private readonly RecordingGateway gateway = new();
    private readonly RecordingEmail email = new();

    [Fact]
    public async Task An_event_about_a_card_that_is_gone_does_nothing()
    {
        store.Context = null;

        await HandleAsync(Blocked);

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task An_event_nobody_needs_to_hear_about_stores_nothing()
    {
        await HandleAsync(Blocked with { Type = BoardEvent.CardMoved });

        Assert.Empty(store.Saved);
        Assert.Empty(gateway.InboxPushes);
    }

    [Fact]
    public async Task Each_recipient_gets_a_stored_notification_and_a_push_to_their_inbox()
    {
        await HandleAsync(Blocked);

        Assert.Equal([Creator, Manager], store.Saved.Select(saved => saved.UserId));
        Assert.All(store.Saved, saved => Assert.Equal(
            (Blocked.BoardId, Blocked.CardId, "outbox:42", "#12 Checkout is blocked"),
            (saved.BoardId, saved.CardId, saved.DedupeKey, saved.Planned.Title)));
        Assert.Equal(Now, store.SavedAt);
        Assert.Equal([Creator, Manager], gateway.InboxPushes);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_notifies_once()
    {
        await HandleAsync(Blocked);
        await HandleAsync(Blocked);

        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(2, gateway.InboxPushes.Count);
    }

    [Fact]
    public async Task Email_goes_to_people_who_asked_for_it_and_have_an_address()
    {
        store.Profiles[Manager] = Profile(Manager, "marco@example.test", Both);
        store.Profiles[Creator] = Profile(Creator, email: null, Both);

        await HandleAsync(Blocked);

        Assert.Equal([new EmailMessage("marco@example.test", "#12 Checkout is blocked", "Waiting for legal")], email.Sent);
        Assert.Equal([Creator, Manager], gateway.InboxPushes);
    }

    [Fact]
    public async Task Someone_who_chose_email_only_is_not_shown_or_pushed_the_notification()
    {
        store.Profiles[Manager] = Profile(Manager, "marco@example.test", new Channels(InApp: false, Email: true));

        await HandleAsync(Blocked);

        Assert.False(store.Saved.Single(saved => saved.UserId == Manager).Channels.InApp);
        Assert.Equal([Creator], gateway.InboxPushes);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Someone_who_turned_a_kind_off_everywhere_gets_nothing()
    {
        store.Profiles[Manager] = Profile(Manager, "marco@example.test", new Channels(InApp: false, Email: false));

        await HandleAsync(Blocked);

        Assert.Equal([Creator], store.Saved.Select(saved => saved.UserId));
        Assert.Empty(email.Sent);
    }

    private Task HandleAsync(BoardEvent boardEvent) =>
        new NotificationGenerator(store, new NotificationSender(store, gateway, email, new FixedClock(Now)))
            .HandleAsync(boardEvent, TestContext.Current.CancellationToken);

    private static RecipientProfile Profile(Guid userId, string? email, Channels forBlocked) =>
        new(userId, email, new Dictionary<NotificationKind, Channels> { [NotificationKind.CardBlocked] = forBlocked });

    private sealed class RecordingEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps notifications in memory and, like the table, refuses a second one with the same key for the same person.</summary>
    private sealed class FakeStore : INotificationStore
    {
        public CardEventContext? Context { get; set; }

        public Dictionary<Guid, RecipientProfile> Profiles { get; } = [];

        public List<Notification> Saved { get; } = [];

        public DateTimeOffset? SavedAt { get; private set; }

        public Task<CardEventContext?> FindContextAsync(BoardEvent boardEvent, CancellationToken cancellationToken) =>
            Task.FromResult(Context);

        public Task<IReadOnlyList<RecipientProfile>> FindProfilesAsync(IReadOnlyList<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecipientProfile>>([.. userIds.Where(Profiles.ContainsKey).Select(user => Profiles[user])]);

        public Task<IReadOnlyList<Notification>> SaveNewAsync(
            IReadOnlyList<Notification> notifications, DateTimeOffset now, CancellationToken cancellationToken)
        {
            List<Notification> added = [.. notifications.Where(IsNew)];
            Saved.AddRange(added);
            SavedAt = now;
            return Task.FromResult<IReadOnlyList<Notification>>(added);
        }

        private bool IsNew(Notification notification) =>
            !Saved.Exists(saved => saved.UserId == notification.UserId && saved.DedupeKey == notification.DedupeKey);
    }
}
