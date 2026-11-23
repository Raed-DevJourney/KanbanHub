using System.Security.Claims;
using KanbanHub.Api.Tests.Infrastructure;
using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Notifications;
using KanbanHub.Domain.Security;
using KanbanHub.TestSupport;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Ai;

/// <summary>
/// Aging and SLE alerts (section 11.1, feature 4). Five finished cards that took 1 to 5 days put the 85th
/// percentile of the board's cycle time at 4.4 days; a card in flow for longer than that is aging.
/// The rule looks at every board, so these tests stay in one class and run one after another.
/// </summary>
public class AgingAlertTests(KanbanDatabase database)
{
    private const string RuleText = "In flow for 10 days. 85% of Feature cards on this board finish within 4.4.";

    [Fact]
    public async Task A_card_in_flow_longer_than_85_percent_of_its_kind_is_alerted_once_to_its_creator_and_the_managers()
    {
        AiScenario ai = await AiScenario.StartAsync(database, optedIn: false);
        ClaimsPrincipal creator = await ai.Board.UserWithAsync(BuiltInRoles.Contributor);
        Guid aging = await AgingCardAsync(ai, by: creator);

        await RaiseAsync(ai);
        await RaiseAsync(ai);   // fifteen minutes later: the card is still aging, and nobody is told again

        NotificationDto forCreator = Assert.Single((await ai.Board.InboxOfAsync(creator)).Notifications);
        NotificationDto forOwner = Assert.Single((await ai.Board.InboxOfAsync(ai.Board.Owner)).Notifications);
        Assert.Equal((NotificationKind.CardAging, $"#{await ai.NumberOfAsync(aging)} Slow one is aging", RuleText, aging),
            (forCreator.Kind, forCreator.Title, forCreator.Detail, forCreator.CardId));
        Assert.Equal((NotificationKind.CardAging, RuleText), (forOwner.Kind, forOwner.Detail));
        Assert.Empty(ai.Model.Requests);   // the rule needs no model, and this board did not opt in
    }

    [Fact]
    public async Task On_a_board_that_uses_ai_the_model_words_the_alert_from_the_rules_numbers()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        Guid aging = await AgingCardAsync(ai);

        await RaiseAsync(ai);

        NotificationDto alert = Assert.Single((await ai.Board.InboxOfAsync(ai.Board.Owner)).Notifications);
        AgingCard told = Assert.IsType<AgingCard>(Assert.Single(ai.Model.Requests).Data);
        Assert.Equal((10m, 4.4m, 5, "Feature", "Implement"), (told.AgeDays, told.ExpectedDays, told.FinishedCards, told.CardType, told.Step));
        Assert.StartsWith($"#{await ai.NumberOfAsync(aging)} has been in flow for 10 days; 85% of Feature cards finish within 4.4.", alert.Detail, StringComparison.Ordinal);
    }

    /// <summary>The model only words the alert. When its wording fails the grounding check, the rule's own sentence goes out.</summary>
    [Fact]
    public async Task An_explanation_that_invents_a_number_is_replaced_by_the_rules_own_sentence()
    {
        AiScenario ai = await AiScenario.StartAsync(database);
        await AgingCardAsync(ai);
        ai.Model.Answer(_ => new AgingExplanation("This card has been stuck for 99 days."));

        await RaiseAsync(ai);

        Assert.Equal(RuleText, Assert.Single((await ai.Board.InboxOfAsync(ai.Board.Owner)).Notifications).Detail);
    }

    [Fact]
    public async Task A_card_within_the_expectation_or_already_finished_is_left_alone()
    {
        AiScenario ai = await AiScenario.StartAsync(database, optedIn: false);
        await FinishFiveAsync(ai);
        Guid young = await ai.AddCardAtAsync("Young one", ai.Board.Implement, Location.Active);
        await ai.StartedAsync(young, daysAgo: 4);

        await RaiseAsync(ai);

        Assert.Empty((await ai.Board.InboxOfAsync(ai.Board.Owner)).Notifications);
    }

    /// <summary>Four finished cards say too little to hold a fifth against them.</summary>
    [Fact]
    public async Task Without_enough_finished_cards_of_the_type_there_is_no_expectation_and_no_alert()
    {
        AiScenario ai = await AiScenario.StartAsync(database, optedIn: false);
        foreach (int days in new[] { 1, 2, 3, 4 }) await ai.FinishedInAsync(days);
        Guid old = await ai.AddCardAtAsync("Slow one", ai.Board.Implement, Location.Active);
        await ai.StartedAsync(old, daysAgo: 30);

        await RaiseAsync(ai);

        Assert.Empty((await ai.Board.InboxOfAsync(ai.Board.Owner)).Notifications);
    }

    [Fact]
    public async Task Someone_who_muted_the_card_or_switched_aging_alerts_off_is_not_told()
    {
        AiScenario ai = await AiScenario.StartAsync(database, optedIn: false);
        ClaimsPrincipal flowManager = await ai.Board.UserWithAsync(BuiltInRoles.FlowManager);
        ClaimsPrincipal creator = await ai.Board.UserWithAsync(BuiltInRoles.Contributor);
        Guid aging = await AgingCardAsync(ai, by: creator);
        await ai.Board.Inbox.Mute(Requests.From(flowManager), aging, Ct);
        await ai.Board.Inbox.SetPreference(
            Requests.From(creator, new SetNotificationPreferenceRequest(NotificationKind.CardAging, InApp: false, Email: false)), Ct);

        await RaiseAsync(ai);

        Assert.Empty((await ai.Board.InboxOfAsync(flowManager)).Notifications);
        Assert.Empty((await ai.Board.InboxOfAsync(creator)).Notifications);
        Assert.Single((await ai.Board.InboxOfAsync(ai.Board.Owner)).Notifications);
    }

    private static Task<int> RaiseAsync(AiScenario ai) => ai.Board.Service<AgingAlerts>().RaiseAsync(Ct);

    /// <summary>Five finished cards that took 1 to 5 days, and one in Implement that was started ten days ago.</summary>
    private static async Task<Guid> AgingCardAsync(AiScenario ai, ClaimsPrincipal? by = null)
    {
        await FinishFiveAsync(ai);
        Guid card = await ai.Board.AddCardAsync(by, title: "Slow one");
        await ai.Board.WalkToAsync(card, ai.Board.Implement, Location.Active);
        await ai.StartedAsync(card, daysAgo: 10);
        return card;
    }

    private static async Task FinishFiveAsync(AiScenario ai)
    {
        foreach (int days in new[] { 1, 2, 3, 4, 5 }) await ai.FinishedInAsync(days);
    }
}
