using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Notifications;

namespace KanbanHub.Domain.Tests.Ai;

public class AgingRuleTests
{
    private static readonly AgingCard Card = new(Guid.NewGuid(), Guid.NewGuid(), 12, "Checkout", "Feature", "Implement")
    {
        AgeDays = 10,
        ExpectedDays = 4.4m,
        FinishedCards = 5
    };

    private static readonly AgingExplanationTask Task = new();

    [Fact]
    public void The_rule_says_in_one_sentence_what_it_compared()
    {
        Assert.Equal("In flow for 10 days. 85% of Feature cards on this board finish within 4.4.", AgingRule.Describe(Card));
        Assert.Equal(
            "In flow for 2.5 days. 85% of Bug cards on this board finish within 1.",
            AgingRule.Describe(Card with { AgeDays = 2.5m, ExpectedDays = 1.0m, CardType = "Bug" }));
    }

    [Theory]
    [InlineData("#12 has been in flow for 10 days; 85% of Feature cards finish within 4.4. Is it too big?", true)]
    [InlineData("Drawn from 5 finished cards, the expectation is 4.4 days; this one is at 10.", true)]
    [InlineData("This card has been stuck for 99 days.", false)]             // a number the rule didn't supply
    [InlineData("It waits on #7, which is blocked.", false)]                  // a card the model wasn't shown
    [InlineData("", false)]
    public void An_explanation_may_use_only_the_rules_own_numbers_and_this_card(string explanation, bool expected)
    {
        Assert.Equal(expected, Task.IsGrounded(Card, new AgingExplanation(explanation)));
    }

    [Fact]
    public void An_explanation_must_fit_in_a_notification()
    {
        string tooLong = new('a', NotificationPlanner.MaxDetailLength + 1);

        Assert.False(Task.IsGrounded(Card, new AgingExplanation(tooLong)));
    }

    [Fact]
    public void An_aging_card_concerns_its_creator_and_the_managers_except_those_who_muted_it()
    {
        Guid creator = Guid.NewGuid(), manager = Guid.NewGuid(), mutedManager = Guid.NewGuid();
        var people = new CardEventContext
        {
            CardNumber = 12,
            CardTitle = "Checkout",
            CreatedBy = creator,
            ActorId = creator,            // the card's last editor: nobody "did" the aging, so they are told too
            Managers = [manager, mutedManager, creator],
            Muted = [mutedManager]
        };

        IReadOnlyList<PlannedNotification> planned = NotificationPlanner.PlanAging(people, "In flow for 10 days.");

        Assert.Equal([creator, manager], planned.Select(notification => notification.UserId));
        Assert.All(planned, notification => Assert.Equal(
            (NotificationKind.CardAging, "#12 Checkout is aging", "In flow for 10 days."),
            (notification.Kind, notification.Title, notification.Detail)));
    }

    [Fact]
    public void Aging_alerts_can_be_switched_off_and_arrive_in_the_app_by_default()
    {
        Assert.True(ChannelPolicy.CanMute(NotificationKind.CardAging));
        Assert.Equal(new Channels(InApp: true, Email: false), ChannelPolicy.DefaultFor(NotificationKind.CardAging));
    }
}

public class DuplicateRuleTests
{
    [Fact]
    public void A_card_is_compared_by_its_title_and_description()
    {
        var card = new CardText(Guid.NewGuid(), "Checkout crashes", "On an empty cart.", EmbeddedHash: null);

        Assert.Equal("Checkout crashes\n\nOn an empty cart.", DuplicateRule.TextOf(card));
        Assert.Equal("Checkout crashes", DuplicateRule.TextOf(card with { Description = null }));
    }

    [Fact]
    public void A_very_long_description_is_cut_before_it_is_embedded()
    {
        var card = new CardText(Guid.NewGuid(), "Title", new string('x', 20_000), EmbeddedHash: null);

        Assert.Equal(DuplicateRule.MaxTextLength, DuplicateRule.TextOf(card).Length);
    }

    [Fact]
    public void The_same_text_hashes_the_same_and_any_change_shows()
    {
        Assert.Equal(DuplicateRule.Hash("Checkout crashes"), DuplicateRule.Hash("Checkout crashes"));
        Assert.NotEqual(DuplicateRule.Hash("Checkout crashes"), DuplicateRule.Hash("Checkout crashes."));
        Assert.Equal(32, DuplicateRule.Hash("").Length);
    }

    [Theory]
    [InlineData(0.0, 100)]
    [InlineData(0.126, 87)]
    [InlineData(0.15, 85)]
    [InlineData(1.0, 0)]
    [InlineData(1.7, 0)]      // opposite vectors: cosine distance runs to 2
    public void Similarity_is_the_distance_turned_into_a_percentage(double distance, int percent)
    {
        Assert.Equal(percent, new NearCard(Guid.NewGuid(), distance).SimilarityPercent);
    }
}
