using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.Domain.Tests.Cards;

/// <summary>Specification section 6.1: one status colors the card, the others still show as badges.</summary>
public class CardStatusTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    [Theory]
    [InlineData(Location.Backlog, false, CardStatus.NotStarted)]
    [InlineData(Location.Active, false, CardStatus.InProgress)]
    [InlineData(Location.Done, false, CardStatus.WaitingForPull)]
    [InlineData(Location.Done, true, CardStatus.Done)]
    [InlineData(Location.Track, false, CardStatus.Tracked)]
    [InlineData(Location.Parked, false, CardStatus.Parked)]
    [InlineData(Location.Canceled, false, CardStatus.Canceled)]
    public void Position_alone_gives_one_status(Location location, bool inFinalStep, CardStatus expected) =>
        Assert.Equal([expected], CardStatuses.Of(new CardSituation(location, inFinalStep, IsBlocked: false, DueDate: null), Today));

    [Fact]
    public void Blocked_outranks_late_which_outranks_position()
    {
        var card = new CardSituation(Location.Active, IsInFinalStep: false, IsBlocked: true, DueDate: Today.AddDays(-1));

        Assert.Equal([CardStatus.Blocked, CardStatus.Late, CardStatus.InProgress], CardStatuses.Of(card, Today));
    }

    [Fact]
    public void A_card_due_today_is_not_late_yet() =>
        Assert.DoesNotContain(CardStatus.Late, CardStatuses.Of(new CardSituation(Location.Active, false, false, Today), Today));

    [Fact]
    public void Finished_and_canceled_work_is_never_late()
    {
        DateOnly overdue = Today.AddDays(-30);

        Assert.Equal([CardStatus.Done], CardStatuses.Of(new CardSituation(Location.Done, true, false, overdue), Today));
        Assert.Equal([CardStatus.Canceled], CardStatuses.Of(new CardSituation(Location.Canceled, false, false, overdue), Today));
    }

    [Fact]
    public void Work_waiting_for_pull_can_still_be_late() =>
        Assert.Equal(
            [CardStatus.Late, CardStatus.WaitingForPull],
            CardStatuses.Of(new CardSituation(Location.Done, false, false, Today.AddDays(-1)), Today));

    [Theory]
    [InlineData(CardStatus.Blocked, "status.blocked")]
    [InlineData(CardStatus.WaitingForPull, "status.waitingForPull")]
    [InlineData(CardStatus.AtRisk, "status.atRisk")]
    public void Each_status_names_its_palette_token(CardStatus status, string token) =>
        Assert.Equal(token, status.TokenKey());

    [Fact]
    public void Every_status_has_a_color_in_the_default_palette() =>
        Assert.All(Enum.GetValues<CardStatus>(), status => Assert.Contains(status.TokenKey(), Domain.Boards.Palette.Defaults.Keys));
}
