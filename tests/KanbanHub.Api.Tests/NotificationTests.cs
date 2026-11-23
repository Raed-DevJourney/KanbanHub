using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Mvc;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests;

/// <summary>
/// From a change on a card to the inboxes of the people it concerns: the endpoints, the outbox events,
/// the notification rules and the real tables together. Only SignalR and email are stand-ins.
/// </summary>
public class NotificationTests(KanbanDatabase database)
{
    private static readonly Blocker WaitingForLegal = new("Waiting for legal", BlockerCategory.External);

    [Fact]
    public async Task Blocking_a_card_tells_its_creator_and_the_other_managers_but_not_whoever_blocked_it()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal creator = await board.UserWithAsync(BuiltInRoles.Contributor);
        ClaimsPrincipal manager = await board.UserWithAsync(BuiltInRoles.FlowManager);
        Guid card = await CardInProgressAsync(board, creator, "Checkout");

        await board.Cards.Block(Requests.From(manager, WaitingForLegal), card, Ct);
        await board.PublishEventsOfAsync(card);

        NotificationDto told = Assert.Single((await board.InboxOfAsync(creator)).Notifications);
        Assert.Equal(
            (board.BoardId, card, NotificationKind.CardBlocked, "Waiting for legal", false),
            (told.BoardId, told.CardId!.Value, told.Kind, told.Detail, told.IsRead));
        Assert.EndsWith(" Checkout is blocked", told.Title, StringComparison.Ordinal);
        Assert.Equal(1, (await board.InboxOfAsync(board.Owner)).UnreadCount);
        Assert.Empty((await board.InboxOfAsync(manager)).Notifications);
        Assert.Equal(new[] { creator.GetObjectId(), board.Owner.GetObjectId() }.Order(), board.Gateway.InboxPushes.Order());
    }

    [Fact]
    public async Task Every_change_to_a_card_tells_the_people_watching_its_board()
    {
        BoardScenario board = await StartAsync(database);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);
        await board.Cards.Block(Requests.From(board.Owner, WaitingForLegal), card, Ct);

        await board.PublishEventsOfAsync(card);

        // Created, pulled into Specify, blocked, and set aside in the Blocked sub-column by that.
        Assert.Equal(
            ["card.created", BoardEvent.CardMoved, BoardEvent.CardBlocked, BoardEvent.CardMoved],
            board.Gateway.BoardChanges.Select(change => change.EventType));
        Assert.All(board.Gateway.BoardChanges, change => Assert.Equal((board.BoardId, card), (change.BoardId, change.CardId)));
    }

    [Fact]
    public async Task Ticking_a_done_rule_item_leaves_an_event_so_watchers_see_it()
    {
        BoardScenario board = await StartAsync(database);
        var rule = new SetDoneRuleRequest([new DoneRuleItemDraft("Reviewed", IsRequired: true)]);
        await board.Settings.SetDoneRule(Requests.From(board.Owner, rule), board.BoardId, board.Specify.Id, Ct);
        Guid card = await board.AddCardAtAsync(board.Specify, Location.Active);
        Guid item = (await board.CardAsync(card)).Checklist[0].Id;

        await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, item, Ct);
        await board.Cards.ToggleChecklistItem(Requests.From(board.Owner), card, Guid.NewGuid(), Ct);   // no such item: no event

        List<BoardEvent> events = await database.Connections.EventsOfCardAsync(card);
        Assert.Equal(["card.created", BoardEvent.CardMoved, "card.updated"], events.Select(written => written.Type));
    }

    [Fact]
    public async Task A_notification_is_read_snoozed_and_swept_away_by_its_owner_only()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal creator = await board.UserWithAsync(BuiltInRoles.Contributor);
        Guid card = await CardInProgressAsync(board, creator, "Checkout");
        await board.Cards.Block(Requests.From(board.Owner, WaitingForLegal), card, Ct);
        await board.Cards.Unblock(Requests.From(board.Owner), card, Ct);
        await board.PublishEventsOfAsync(card);
        InboxDto inbox = await board.InboxOfAsync(creator);
        long latest = inbox.Notifications[0].Id;

        (await board.Inbox.MarkRead(Requests.From(board.Owner), latest, Ct)).ProblemOf(404, "not-found");
        IActionResult read = await board.Inbox.MarkRead(Requests.From(creator), latest, Ct);
        InboxDto afterReading = await board.InboxOfAsync(creator);

        (await board.Inbox.Snooze(Requests.From(creator, new SnoozeRequest(0)), latest, Ct)).ProblemOf(422, "validation-failed");
        (await board.Inbox.Snooze(Requests.From(creator), latest, Ct)).ProblemOf(400, "body-required");
        (await board.Inbox.Snooze(Requests.From(board.Owner, new SnoozeRequest(2)), latest, Ct)).ProblemOf(404, "not-found");
        IActionResult snoozed = await board.Inbox.Snooze(Requests.From(creator, new SnoozeRequest(2)), latest, Ct);
        InboxDto afterSnoozing = await board.InboxOfAsync(creator);

        IActionResult swept = await board.Inbox.MarkAllRead(Requests.From(creator), Ct);
        InboxDto afterSweeping = await board.InboxOfAsync(creator);

        Assert.Equal([NotificationKind.CardUnblocked, NotificationKind.CardBlocked], inbox.Notifications.Select(shown => shown.Kind));
        Assert.Equal((2, 204, 1), (inbox.UnreadCount, read.StatusOf(), afterReading.UnreadCount));
        Assert.Equal((204, NotificationKind.CardBlocked), (snoozed.StatusOf(), Assert.Single(afterSnoozing.Notifications).Kind));
        Assert.Equal((204, 0), (swept.StatusOf(), afterSweeping.UnreadCount));
    }

    [Fact]
    public async Task Muting_a_card_silences_it_for_that_person_until_they_unmute_it()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal creator = await board.UserWithAsync(BuiltInRoles.Contributor);
        Guid card = await CardInProgressAsync(board, creator, "Checkout");

        IActionResult muted = await board.Inbox.Mute(Requests.From(creator), card, Ct);
        await board.Cards.Block(Requests.From(board.Owner, WaitingForLegal), card, Ct);
        await board.PublishEventsOfAsync(card);
        InboxDto whileMuted = await board.InboxOfAsync(creator);

        IActionResult unmuted = await board.Inbox.Unmute(Requests.From(creator), card, Ct);
        await board.Cards.Unblock(Requests.From(board.Owner), card, Ct);
        await board.PublishEventsOfAsync(card);
        InboxDto afterUnmuting = await board.InboxOfAsync(creator);

        Assert.Equal((204, 204), (muted.StatusOf(), unmuted.StatusOf()));
        Assert.Empty(whileMuted.Notifications);
        Assert.Equal(NotificationKind.CardUnblocked, Assert.Single(afterUnmuting.Notifications).Kind);
    }

    [Fact]
    public async Task Muting_needs_a_card_the_person_can_read()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal stranger = await board.UserWithAsync(role: null);
        Guid card = await board.AddCardAsync();

        (await board.Inbox.Mute(Requests.From(stranger), card, Ct)).ProblemOf(403, "forbidden");
        (await board.Inbox.Mute(Requests.From(board.Owner), Guid.NewGuid(), Ct)).ProblemOf(404, "not-found");
    }

    [Fact]
    public async Task Channels_default_to_the_inbox_and_follow_what_the_person_chooses()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal user = TestUsers.WithTenantRoles();

        IReadOnlyList<NotificationPreferenceDto> defaults = await PreferencesOfAsync(board, user);
        IActionResult saved = await board.Inbox.SetPreference(
            Requests.From(user, new SetNotificationPreferenceRequest(NotificationKind.CardBlocked, InApp: false, Email: true)), Ct);
        IReadOnlyList<NotificationPreferenceDto> chosen = await PreferencesOfAsync(board, user);

        Assert.Equal(Enum.GetValues<NotificationKind>(), defaults.Select(preference => preference.Kind));
        Assert.All(defaults, preference => Assert.Equal((true, false), (preference.InApp, preference.Email)));
        Assert.Equal([NotificationKind.WipOverrideUsed], defaults.Where(preference => !preference.CanTurnOffInApp).Select(preference => preference.Kind));
        Assert.Equal(204, saved.StatusOf());
        Assert.Equal(
            new NotificationPreferenceDto(NotificationKind.CardBlocked, InApp: false, Email: true, CanTurnOffInApp: true),
            chosen.Single(preference => preference.Kind == NotificationKind.CardBlocked));
    }

    [Fact]
    public async Task A_wip_override_cannot_be_turned_off_in_the_app()
    {
        BoardScenario board = await StartAsync(database);
        var silenced = new SetNotificationPreferenceRequest(NotificationKind.WipOverrideUsed, InApp: false, Email: false);

        (await board.Inbox.SetPreference(Requests.From(board.Owner, silenced), Ct)).ProblemOf(422, "validation-failed");
        (await board.Inbox.SetPreference(Requests.From(board.Owner), Ct)).ProblemOf(400, "body-required");
    }

    [Fact]
    public async Task Someone_who_asked_for_email_gets_it_at_the_address_they_signed_in_with()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal creator = TestUsers.WithEmail("carl@example.test");
        await board.AssignAsync(creator.GetObjectId(), PrincipalType.User, BuiltInRoles.Contributor);
        await board.Identity.Me(Requests.From(creator), Ct);   // signing in is when the address is learned
        await board.Inbox.SetPreference(
            Requests.From(creator, new SetNotificationPreferenceRequest(NotificationKind.CardBlocked, InApp: true, Email: true)), Ct);
        Guid card = await CardInProgressAsync(board, creator, "Checkout");

        await board.Cards.Block(Requests.From(board.Owner, WaitingForLegal), card, Ct);
        await board.PublishEventsOfAsync(card);

        EmailMessage sent = Assert.Single(board.Email.Sent);
        Assert.Equal(("carl@example.test", "Waiting for legal"), (sent.To, sent.Body));
        Assert.EndsWith(" Checkout is blocked", sent.Subject, StringComparison.Ordinal);
        Assert.Single((await board.InboxOfAsync(creator)).Notifications);
    }

    [Fact]
    public async Task Signing_in_remembers_the_persons_name_and_address()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal user = TestUsers.WithEmail("rita@example.test");

        await board.Identity.Me(Requests.From(user), Ct);

        (string Name, string? Email) remembered = await database.Connections.QuerySingleAsync(
            "SELECT DisplayName, Email FROM kb.AppUser WHERE UserId = @UserId;",
            new { UserId = user.GetObjectId() }, row => (row.GetString(0), row.GetNullableString(1)));
        Assert.Equal(("Test user", "rita@example.test"), remembered);
    }

    [Fact]
    public async Task A_comment_tells_the_cards_creator_and_those_already_in_the_conversation()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal creator = await board.UserWithAsync(BuiltInRoles.Contributor);
        Guid card = await board.AddCardAsync(creator, "Checkout");

        await board.Comments.Add(Requests.From(board.Owner, new AddCommentRequest("Is this still needed?")), card, Ct);
        await board.PublishEventsOfAsync(card);
        await board.Comments.Add(Requests.From(creator, new AddCommentRequest("Yes, for the audit.")), card, Ct);
        await board.PublishEventsOfAsync(card);

        NotificationDto forCreator = Assert.Single((await board.InboxOfAsync(creator)).Notifications);
        NotificationDto forOwner = Assert.Single((await board.InboxOfAsync(board.Owner)).Notifications);
        Assert.Equal((NotificationKind.CommentAdded, "Is this still needed?"), (forCreator.Kind, forCreator.Detail));
        Assert.Equal((NotificationKind.CommentAdded, "Yes, for the audit."), (forOwner.Kind, forOwner.Detail));
    }

    [Fact]
    public async Task Overriding_a_wip_limit_tells_the_boards_other_managers_why()
    {
        BoardScenario board = await StartAsync(database);
        ClaimsPrincipal manager = await board.UserWithAsync(BuiltInRoles.FlowManager);
        await board.SetLimitsAsync(board.Owner, new StepLimit(board.Implement.Id, 1, WipPolicy.AllowWithReason));
        await board.AddCardAtAsync(board.Implement, Location.Active);
        Guid pulled = await board.AddCardAtAsync(board.Specify, Location.Done);

        IActionResult moved = await board.MoveAsync(manager, pulled, board.Implement, Location.Active, reason: "Production hotfix");
        await board.PublishEventsOfAsync(pulled);

        Assert.Equal(200, moved.StatusOf());
        NotificationDto told = Assert.Single((await board.InboxOfAsync(board.Owner)).Notifications);
        Assert.Equal((NotificationKind.WipOverrideUsed, "Production hotfix"), (told.Kind, told.Detail));
        Assert.Empty((await board.InboxOfAsync(manager)).Notifications);
    }

    /// <summary>A card its creator added and the owner then pulled into the first step.</summary>
    private static async Task<Guid> CardInProgressAsync(BoardScenario board, ClaimsPrincipal creator, string title)
    {
        Guid card = await board.AddCardAsync(creator, title);
        await board.WalkToAsync(card, board.Specify, Location.Active);
        return card;
    }

    private static async Task<IReadOnlyList<NotificationPreferenceDto>> PreferencesOfAsync(BoardScenario board, ClaimsPrincipal user) =>
        (await board.Inbox.GetPreferences(Requests.From(user), Ct)).ValueOf<IReadOnlyList<NotificationPreferenceDto>>();
}
