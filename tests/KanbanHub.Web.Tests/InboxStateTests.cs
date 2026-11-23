using System.Net;
using KanbanHub.Contracts;
using KanbanHub.Domain.Notifications;
using KanbanHub.Web.State;
using KanbanHub.Web.Tests.Infrastructure;

namespace KanbanHub.Web.Tests;

/// <summary>The inbox on screen: what it shows and what each action sends.</summary>
public sealed class InboxStateTests : IDisposable
{
    private const string Inbox = "me/notifications";

    private static readonly Guid Card = Guid.NewGuid();
    private static readonly NotificationDto Unread = Notification(1, isRead: false);
    private static readonly NotificationDto Read = Notification(2, isRead: true);

    private readonly StubServer server = new();
    private readonly Notices notices = new();
    private readonly InboxState inbox;

    public InboxStateTests()
    {
        server.OnGet(Inbox, new InboxDto([Unread, Read], UnreadCount: 1));
        inbox = new InboxState(server.CreateApi(), notices);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => server.Dispose();

    [Fact]
    public async Task Loading_shows_the_notifications_and_counts_the_unread()
    {
        int announced = 0;
        inbox.Changed += () => announced++;

        await inbox.LoadAsync(Ct);

        Assert.Equal([1L, 2L], inbox.Notifications.Select(shown => shown.Id));
        Assert.Equal((1, 1), (inbox.UnreadCount, announced));
    }

    [Fact]
    public async Task Marking_read_tells_the_server_and_reloads()
    {
        server.On(HttpMethod.Post, "me/notifications/1/read", HttpStatusCode.NoContent);

        await inbox.MarkReadAsync(Unread, Ct);

        Assert.Equal(["me/notifications/1/read", Inbox], server.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task Marking_all_read_and_snoozing_send_what_the_server_expects()
    {
        server.On(HttpMethod.Post, "me/notifications/read-all", HttpStatusCode.NoContent);
        server.On(HttpMethod.Post, "me/notifications/1/snooze", HttpStatusCode.NoContent);

        await inbox.MarkAllReadAsync(Ct);
        await inbox.SnoozeAsync(Unread, InboxState.ADay, Ct);

        Assert.Equal("me/notifications/read-all", server.Requests[0].Path);
        Assert.Equal(("me/notifications/1/snooze", """{"hours":24}"""), (server.Last(HttpMethod.Post).Path, server.Last(HttpMethod.Post).Body));
    }

    [Fact]
    public async Task Muting_a_card_mutes_it_files_the_notification_and_says_so()
    {
        server.On(HttpMethod.Put, $"cards/{Card}/mute", HttpStatusCode.NoContent);
        server.On(HttpMethod.Post, "me/notifications/1/read", HttpStatusCode.NoContent);

        await inbox.MuteCardAsync(Unread, Ct);

        Assert.Equal([$"cards/{Card}/mute", "me/notifications/1/read", Inbox], server.Requests.Select(request => request.Path));
        Assert.Equal(NoticeKind.Success, Assert.Single(notices.Toasts).Kind);
    }

    [Fact]
    public async Task A_card_that_cannot_be_muted_is_explained_and_the_notification_stays()
    {
        server.OnProblem(HttpMethod.Put, $"cards/{Card}/mute", 403, "forbidden", "Missing permission: BoardRead.");

        await inbox.MuteCardAsync(Unread, Ct);

        Assert.Equal("Missing permission: BoardRead.", Assert.Single(notices.Toasts).Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_refused_change_is_explained()
    {
        server.OnProblem(HttpMethod.Post, "me/notifications/1/read", 404, "not-found", "No such notification.");

        await inbox.MarkReadAsync(Unread, Ct);

        Assert.Equal((NoticeKind.Problem, "No such notification."), (notices.Toasts[0].Kind, notices.Toasts[0].Message));
    }

    [Fact]
    public async Task New_mail_is_announced_for_screen_readers_but_the_first_load_is_not()
    {
        await inbox.LoadAsync(Ct);
        string afterFirstLoad = notices.Announcement;
        server.OnGet(Inbox, new InboxDto([Notification(3, isRead: false), Unread, Read], UnreadCount: 2));

        await inbox.LoadAsync(Ct);

        Assert.Equal("", afterFirstLoad);
        Assert.Equal("New notification. 2 unread in your inbox.", notices.Announcement);
    }

    [Fact]
    public void Every_kind_of_notification_has_words_for_people()
    {
        Assert.All(Enum.GetValues<NotificationKind>(), kind => Assert.NotEqual(kind.ToString(), NotificationLabels.Of(kind)));
    }

    private static NotificationDto Notification(long id, bool isRead) => new()
    {
        Id = id,
        BoardId = SampleBoard.Id,
        CardId = Card,
        Kind = NotificationKind.CardBlocked,
        Title = "#12 Checkout is blocked",
        CreatedAtUtc = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc),
        IsRead = isRead
    };
}
