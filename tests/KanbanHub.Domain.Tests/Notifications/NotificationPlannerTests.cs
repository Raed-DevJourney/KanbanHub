using KanbanHub.Domain.Events;
using KanbanHub.Domain.Notifications;

namespace KanbanHub.Domain.Tests.Notifications;

/// <summary>Who hears about what (the rows of specification section 11.3 this release sends).</summary>
public class NotificationPlannerTests
{
    private static readonly Guid Creator = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly Guid Commenter = Guid.NewGuid();

    private static readonly CardEventContext Card = new()
    {
        CardNumber = 12,
        CardTitle = "Checkout",
        CreatedBy = Creator,
        ActorId = Actor,
        Detail = "Waiting for legal",
        Managers = [Manager],
        Participants = [Commenter]
    };

    [Fact]
    public void A_blocked_card_tells_its_creator_and_the_boards_managers()
    {
        IReadOnlyList<PlannedNotification> planned = Plan(BoardEvent.CardBlocked, Card);

        Assert.Equal([Creator, Manager], planned.Select(notification => notification.UserId));
        Assert.All(planned, notification => Assert.Equal(
            (NotificationKind.CardBlocked, "#12 Checkout is blocked", "Waiting for legal"),
            (notification.Kind, notification.Title, notification.Detail)));
    }

    [Fact]
    public void An_unblocked_card_tells_the_same_people()
    {
        IReadOnlyList<PlannedNotification> planned = Plan(BoardEvent.CardUnblocked, Card);

        Assert.Equal([Creator, Manager], planned.Select(notification => notification.UserId));
        Assert.All(planned, notification => Assert.Equal(
            (NotificationKind.CardUnblocked, "#12 Checkout is unblocked"), (notification.Kind, notification.Title)));
    }

    [Fact]
    public void A_comment_tells_the_creator_and_earlier_commenters_but_not_the_managers()
    {
        IReadOnlyList<PlannedNotification> planned = Plan(BoardEvent.CardCommented, Card);

        Assert.Equal([Creator, Commenter], planned.Select(notification => notification.UserId));
        Assert.All(planned, notification => Assert.Equal(
            (NotificationKind.CommentAdded, "New comment on #12 Checkout"), (notification.Kind, notification.Title)));
    }

    [Fact]
    public void Nobody_is_told_about_their_own_action()
    {
        IReadOnlyList<PlannedNotification> planned = Plan(BoardEvent.CardBlocked, Card with { ActorId = Creator });

        Assert.Equal([Manager], planned.Select(notification => notification.UserId));
    }

    [Fact]
    public void Someone_who_created_the_card_and_manages_the_board_is_told_once()
    {
        IReadOnlyList<PlannedNotification> planned = Plan(BoardEvent.CardBlocked, Card with { Managers = [Creator, Manager] });

        Assert.Equal([Creator, Manager], planned.Select(notification => notification.UserId));
    }

    [Fact]
    public void A_muted_card_stays_quiet_for_whoever_muted_it()
    {
        CardEventContext muted = Card with { Muted = [Creator, Commenter] };

        Assert.Equal([Manager], Plan(BoardEvent.CardBlocked, muted).Select(notification => notification.UserId));
        Assert.Empty(Plan(BoardEvent.CardCommented, muted));
    }

    [Fact]
    public void An_ordinary_move_tells_nobody()
    {
        Assert.Empty(Plan(BoardEvent.CardMoved, Card));
    }

    [Fact]
    public void A_wip_override_tells_the_managers_even_when_they_muted_the_card()
    {
        CardEventContext overridden = Card with { IsWipOverride = true, Detail = "Hotfix", Muted = [Manager] };

        PlannedNotification planned = Assert.Single(Plan(BoardEvent.CardMoved, overridden));

        Assert.Equal(
            new PlannedNotification(Manager, NotificationKind.WipOverrideUsed, "A WIP limit was overridden for #12 Checkout", "Hotfix"),
            planned);
    }

    [Fact]
    public void The_manager_who_overrode_the_limit_is_not_told_about_it()
    {
        Assert.Empty(Plan(BoardEvent.CardMoved, Card with { IsWipOverride = true, ActorId = Manager }));
    }

    [Fact]
    public void A_long_detail_is_cut_to_fit()
    {
        CardEventContext wordy = Card with { Detail = new string('x', 300) };

        string detail = Plan(BoardEvent.CardBlocked, wordy)[0].Detail!;

        Assert.Equal(NotificationPlanner.MaxDetailLength, detail.Length);
        Assert.EndsWith("x…", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("card.created")]
    [InlineData("card.updated")]
    [InlineData("something.else")]
    public void Events_without_a_rule_tell_nobody(string eventType)
    {
        Assert.Empty(Plan(eventType, Card));
    }

    private static IReadOnlyList<PlannedNotification> Plan(string eventType, CardEventContext card) =>
        NotificationPlanner.Plan(new BoardEvent(1, Guid.NewGuid(), Guid.NewGuid(), eventType, "{}"), card);
}
