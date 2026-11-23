using KanbanHub.Contracts;
using KanbanHub.Data.IntegrationTests.Infrastructure;
using KanbanHub.Data.Notifications;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Notifications;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.Notifications;

/// <summary>What the notification rules read from the database, and what the inbox keeps in it.</summary>
public class NotificationStoreTests(KanbanDatabase database) : MoveCardTestBase(database)
{
    private const byte OwnerRole = 1;
    private const byte FlowManagerRole = 2;
    private const byte MemberRole = 3;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

    private readonly NotificationStore store = new(database.Connections);
    private readonly Guid reader = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_context_of_a_blocked_card_names_the_card_the_reason_and_the_people_around_it()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        await Board.BlockAsync(card);
        (Guid owner, Guid manager, Guid member, Guid commenter, Guid muter) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await AssignAsync(owner, OwnerRole);
        await AssignAsync(manager, FlowManagerRole);
        await AssignAsync(member, MemberRole);
        await CommentAsync(card, commenter, "Any news?");
        await store.SetCardMuteAsync(muter, card, isMuted: true, Ct);

        CardEventContext context = await ContextOfAsync(new BoardEvent(1, Board.Id, card, BoardEvent.CardBlocked, "{}"));

        Assert.Equal(("Waiting", TestBoard.Actor, TestBoard.Actor, false), (context.Detail, context.CreatedBy, context.ActorId, context.IsWipOverride));
        Assert.StartsWith("Card ", context.CardTitle, StringComparison.Ordinal);
        Assert.Equal(new[] { owner, manager }.Order(), context.Managers.Order());
        Assert.Equal([commenter], context.Participants);
        Assert.Equal([muter], context.Muted);
    }

    [Fact]
    public async Task Unmuting_a_card_lets_its_notifications_through_again()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        await store.SetCardMuteAsync(reader, card, isMuted: true, Ct);
        await store.SetCardMuteAsync(reader, card, isMuted: true, Ct);   // muting twice is not an error

        await store.SetCardMuteAsync(reader, card, isMuted: false, Ct);

        Assert.Empty((await ContextOfAsync(new BoardEvent(1, Board.Id, card, BoardEvent.CardBlocked, "{}"))).Muted);
    }

    [Fact]
    public async Task A_move_past_an_allow_with_reason_limit_is_a_wip_override_by_whoever_moved()
    {
        await Board.SetLimitAsync(Board.Implement, 1, WipPolicy.AllowWithReason);
        await Board.AddCardAsync(Board.Implement.Active);
        Guid pulled = await Board.AddCardAsync(Board.Specify.Done);
        await MustMoveAsync(pulled, Board.Implement.Active, new MoveOptions { OverrideReason = "Production hotfix" });

        BoardEvent moved = (await Connections.EventsOfCardAsync(pulled)).Last();
        CardEventContext context = await ContextOfAsync(moved);

        Assert.Equal(BoardEvent.CardMoved, moved.Type);
        Assert.Equal((true, "Production hotfix", TestBoard.Actor), (context.IsWipOverride, context.Detail, context.ActorId));
    }

    [Fact]
    public async Task An_ordinary_move_is_not_an_override_and_a_comment_names_its_author_and_text()
    {
        Guid card = await Board.AddCardAsync(Board.Specify.Done);
        await MustMoveAsync(card, Board.Implement.Active);
        Guid author = Guid.NewGuid();
        Guid commentId = await CommentAsync(card, author, "Looks good");

        CardEventContext move = await ContextOfAsync((await Connections.EventsOfCardAsync(card)).Last());
        CardEventContext comment = await ContextOfAsync(
            new BoardEvent(2, Board.Id, card, BoardEvent.CardCommented, $$"""{"cardId":"{{card}}","commentId":"{{commentId}}"}"""));

        Assert.False(move.IsWipOverride);
        Assert.Equal((author, "Looks good"), (comment.ActorId, comment.Detail));
    }

    [Fact]
    public async Task There_is_no_context_for_a_card_that_does_not_exist()
    {
        Assert.Null(await store.FindContextAsync(new BoardEvent(1, Board.Id, Guid.NewGuid(), BoardEvent.CardBlocked, "{}"), Ct));
    }

    [Fact]
    public async Task A_notification_is_stored_once_however_often_its_event_arrives()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        Notification notification = ForReader(card, "outbox:1", "#1 is blocked", inApp: true);

        IReadOnlyList<Notification> first = await store.SaveNewAsync([notification], Now, Ct);
        IReadOnlyList<Notification> again = await store.SaveNewAsync([notification], Now.AddMinutes(1), Ct);

        Assert.Equal([notification], first);
        Assert.Empty(again);
        NotificationDto shown = Assert.Single(await InboxAsync(Now));
        Assert.Equal(
            (Board.Id, card, NotificationKind.CardBlocked, "#1 is blocked", "Waiting", Now.UtcDateTime, false),
            (shown.BoardId, shown.CardId!.Value, shown.Kind, shown.Title, shown.Detail, shown.CreatedAtUtc, shown.IsRead));
    }

    [Fact]
    public async Task The_inbox_is_newest_first_and_leaves_out_what_was_sent_by_email_only()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        await store.SaveNewAsync([ForReader(card, "outbox:1", "First", inApp: true)], Now, Ct);
        await store.SaveNewAsync([ForReader(card, "outbox:2", "Email only", inApp: false)], Now.AddMinutes(1), Ct);
        await store.SaveNewAsync([ForReader(card, "outbox:3", "Latest", inApp: true)], Now.AddMinutes(2), Ct);

        Assert.Equal(["Latest", "First"], (await InboxAsync(Now.AddMinutes(3))).Select(shown => shown.Title));
    }

    [Fact]
    public async Task Reading_marks_one_notification_or_all_of_them_and_never_someone_elses()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        await store.SaveNewAsync([ForReader(card, "outbox:1", "One", true), ForReader(card, "outbox:2", "Two", true), ForReader(card, "outbox:3", "Three", true)], Now, Ct);
        long one = (await InboxAsync(Now)).Single(shown => shown.Title == "One").Id;

        int byAStranger = await store.MarkReadAsync(Guid.NewGuid(), one, Now, Ct);
        int byTheReader = await store.MarkReadAsync(reader, one, Now, Ct);
        IReadOnlyList<NotificationDto> afterOne = await InboxAsync(Now);
        int theRest = await store.MarkReadAsync(reader, notificationId: null, Now, Ct);

        Assert.Equal((0, 1, 3), (byAStranger, byTheReader, theRest));
        Assert.Equal(["One"], afterOne.Where(shown => shown.IsRead).Select(shown => shown.Title));
        Assert.All(await InboxAsync(Now), shown => Assert.True(shown.IsRead));
    }

    [Fact]
    public async Task A_snoozed_notification_is_hidden_until_its_time_comes_and_returns_unread()
    {
        Guid card = await Board.AddCardAsync(Board.Implement.Active);
        await store.SaveNewAsync([ForReader(card, "outbox:1", "Later", inApp: true)], Now, Ct);
        long id = (await InboxAsync(Now)).Single().Id;

        bool snoozed = await store.SnoozeAsync(reader, id, Now.AddHours(1), Ct);
        bool someoneElses = await store.SnoozeAsync(Guid.NewGuid(), id, Now.AddHours(1), Ct);

        int readWhileSnoozed = await store.MarkReadAsync(reader, notificationId: null, Now.AddMinutes(30), Ct);

        Assert.True(snoozed);
        Assert.False(someoneElses);
        Assert.Equal(0, readWhileSnoozed);   // "mark all read" covers what is on screen, not what was put aside
        Assert.Empty(await InboxAsync(Now.AddMinutes(59)));
        Assert.False(Assert.Single(await InboxAsync(Now.AddHours(1))).IsRead);
    }

    [Fact]
    public async Task A_profile_carries_the_latest_email_and_the_channels_the_person_chose()
    {
        Guid stranger = Guid.NewGuid();
        await store.RememberUserAsync(reader, "Rita Reader", "old@example.test", Now, Ct);
        await store.RememberUserAsync(reader, "Rita Reader", "rita@example.test", Now.AddDays(1), Ct);
        await store.SetPreferenceAsync(reader, NotificationKind.CardBlocked, new Channels(InApp: true, Email: true), Ct);
        await store.SetPreferenceAsync(reader, NotificationKind.CardBlocked, new Channels(InApp: false, Email: true), Ct);

        IReadOnlyList<RecipientProfile> profiles = await store.FindProfilesAsync([reader, stranger], Ct);

        Assert.Equal("rita@example.test", profiles[0].Email);
        Assert.Equal(new Channels(InApp: false, Email: true), Assert.Single(profiles[0].Preferences).Value);
        Assert.Equal((stranger, null, 0), (profiles[1].UserId, profiles[1].Email, profiles[1].Preferences.Count));
        Assert.Equal(profiles[0].Preferences, await store.ListPreferencesAsync(reader, Ct));
    }

    private async Task<CardEventContext> ContextOfAsync(BoardEvent boardEvent) =>
        Assert.IsType<CardEventContext>(await store.FindContextAsync(boardEvent, Ct));

    private Task<IReadOnlyList<NotificationDto>> InboxAsync(DateTimeOffset at) => store.ListInboxAsync(reader, at, Ct);

    private Notification ForReader(Guid card, string dedupeKey, string title, bool inApp) =>
        new(reader, Board.Id, card, new PlannedNotification(reader, NotificationKind.CardBlocked, title, "Waiting"),
            new Channels(inApp, Email: !inApp), EmailAddress: null)
        {
            DedupeKey = dedupeKey
        };

    private Task AssignAsync(Guid userId, byte roleId) =>
        Connections.ExecuteAsync("""
            INSERT kb.RoleAssignment (RoleAssignmentId, BoardId, PrincipalId, PrincipalType, RoleId, CreatedAtUtc, CreatedBy)
            VALUES (NEWID(), @BoardId, @UserId, 1, @RoleId, SYSUTCDATETIME(), @UserId);
            """,
            new { BoardId = Board.Id, UserId = userId, RoleId = roleId });

    private async Task<Guid> CommentAsync(Guid card, Guid author, string body)
    {
        Guid commentId = Guid.NewGuid();
        await Connections.ExecuteAsync(
            "INSERT kb.CardComment (CommentId, CardId, Body, AuthorId, CreatedAtUtc) VALUES (@CommentId, @CardId, @Body, @Author, SYSUTCDATETIME());",
            new { CommentId = commentId, CardId = card, Body = body, Author = author });
        return commentId;
    }
}
