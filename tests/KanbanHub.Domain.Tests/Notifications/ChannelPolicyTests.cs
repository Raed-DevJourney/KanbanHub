using KanbanHub.Domain.Notifications;

namespace KanbanHub.Domain.Tests.Notifications;

public class ChannelPolicyTests
{
    private static readonly Channels Nowhere = new(InApp: false, Email: false);

    [Fact]
    public void Without_a_choice_every_kind_goes_to_the_inbox_only()
    {
        Assert.All(Enum.GetValues<NotificationKind>(), kind =>
            Assert.Equal(new Channels(InApp: true, Email: false), ChannelPolicy.Resolve(kind, preference: null)));
    }

    [Fact]
    public void A_persons_choice_wins_over_the_default()
    {
        var emailOnly = new Channels(InApp: false, Email: true);

        Assert.Equal(emailOnly, ChannelPolicy.Resolve(NotificationKind.CardBlocked, emailOnly));
        Assert.False(ChannelPolicy.Resolve(NotificationKind.CommentAdded, Nowhere).Any);
    }

    [Fact]
    public void A_wip_override_cannot_be_silenced_in_the_app()
    {
        Assert.False(ChannelPolicy.CanMute(NotificationKind.WipOverrideUsed));
        Assert.Equal(new Channels(InApp: true, Email: false), ChannelPolicy.Resolve(NotificationKind.WipOverrideUsed, Nowhere));
    }

    [Theory]
    [InlineData(NotificationKind.CardBlocked)]
    [InlineData(NotificationKind.CardUnblocked)]
    [InlineData(NotificationKind.CommentAdded)]
    public void Every_other_kind_can_be_turned_off(NotificationKind kind)
    {
        Assert.True(ChannelPolicy.CanMute(kind));
    }
}
