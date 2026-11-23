using KanbanHub.Contracts;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.Web.Api;
using KanbanHub.Web.State;
using KanbanHub.Web.Tests.Infrastructure;
using static KanbanHub.Web.Tests.Infrastructure.SampleBoard;

namespace KanbanHub.Web.Tests;

/// <summary>The small pieces of interaction logic: slots, keyboard carrying, filters, backlog gestures, chip text.</summary>
public class InteractionTests
{
    private static readonly IReadOnlyList<BoardSlot> Slots = BoardSlot.AllOf([Specify, Implement, Validate]);

    [Fact]
    public void A_boards_slots_read_left_to_right_and_Blocked_appears_only_where_offered() =>
        Assert.Equal(
            ["Backlog", "Specify Active", "Specify Done", "Implement Active", "Implement Blocked", "Implement Done",
             "Validate Active", "Validate Done", "Parking lot"],
            Slots.Select(slot => slot.Label));

    [Fact]
    public void A_carried_card_starts_where_it_is_and_walks_one_slot_per_arrow_key()
    {
        var keyboard = new KeyboardMove();
        CardDto card = Card("Built", Implement, Location.Done);

        keyboard.PickUp(card, Slots);
        Assert.Equal("Implement Done", keyboard.Target?.Label);

        keyboard.Step(+1);
        Assert.Equal("Validate Active", keyboard.Target?.Label);

        keyboard.Step(-1);
        keyboard.Step(-1);
        Assert.Equal("Implement Blocked", keyboard.Target?.Label);
    }

    [Fact]
    public void A_carried_card_stops_at_the_edges_of_the_board()
    {
        var keyboard = new KeyboardMove();
        keyboard.PickUp(Card("New", null, Location.Backlog), Slots);

        keyboard.Step(-1);
        Assert.Equal("Backlog", keyboard.Target?.Label);

        for (int i = 0; i < 20; i++) keyboard.Step(+1);
        Assert.Equal("Parking lot", keyboard.Target?.Label);
    }

    [Fact]
    public void Releasing_ends_the_carry_and_says_what_was_carried_where()
    {
        var keyboard = new KeyboardMove();
        CardDto card = Card("Built", Implement, Location.Done);
        keyboard.PickUp(card, Slots);
        keyboard.Step(+1);

        (CardDto Card, BoardSlot Target)? released = keyboard.Release();

        Assert.Equal((card, "Validate Active"), (released?.Card, released?.Target.Label));
        Assert.False(keyboard.IsCarrying);
        Assert.Null(keyboard.Release());
    }

    [Fact]
    public void Filters_combine_and_can_be_cleared()
    {
        var filter = new BoardFilter();
        DateOnly today = new(2026, 11, 16);
        CardDto blockedBug = Card("Fix export", Implement, Location.Active) with { IsBlocked = true, CardType = CardType.Bug };
        CardDto lateFeature = Card("Gift receipts", Implement, Location.Active) with { DueDate = today.AddDays(-1) };

        Assert.True(filter.Matches(blockedBug, today) && filter.Matches(lateFeature, today));

        filter.ToggleBlocked();
        Assert.True(filter.Matches(blockedBug, today));
        Assert.False(filter.Matches(lateFeature, today));

        filter.ToggleBlocked();
        filter.ToggleLate();
        filter.Search("gift");
        Assert.True(filter.Matches(lateFeature, today));
        Assert.False(filter.Matches(blockedBug, today));

        filter.Clear();
        filter.ShowType(CardType.Bug);
        Assert.True(filter.IsActive);
        Assert.False(filter.Matches(lateFeature, today));
    }

    [Fact]
    public void Backlog_gestures_always_send_the_whole_ready_list()
    {
        CardDto first = Card("First", null, Location.Backlog) with { IsReady = true, Rank = 1 };
        CardDto second = Card("Second", null, Location.Backlog) with { IsReady = true, Rank = 2 };
        CardDto unsorted = Card("Unsorted", null, Location.Backlog);
        CardDto[] cards = [second, unsorted, first];

        Assert.Equal([first.Id, second.Id, unsorted.Id], BacklogOrdering.ToggleReady(cards, unsorted).Ready);
        Assert.Equal([second.Id], BacklogOrdering.ToggleReady(cards, first).Ready);
        Assert.Equal([second.Id, first.Id], BacklogOrdering.Shift(cards, first, +1).Ready);
        Assert.Equal([first.Id, second.Id], BacklogOrdering.Shift(cards, first, -1).Ready);   // already on top

        BacklogOrder bucketed = BacklogOrdering.SetBucket(cards, unsorted, 2);
        Assert.Equal([first.Id, second.Id], bucketed.Ready);
        Assert.Equal([new BucketPlacement(unsorted.Id, 2)], bucketed.Buckets);
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#CA8A04", "#000000")]
    [InlineData("#EA580C", "#000000")]
    [InlineData("#2563EB", "#FFFFFF")]
    [InlineData("#0F172A", "#FFFFFF")]
    public void Chip_text_is_black_or_white_whichever_reads_better(string background, string expected)
    {
        Assert.Equal(expected, ChipText.On(background));
        Assert.True(ChipText.ContrastOn(background) >= 4.5);
    }

    [Fact]
    public void Black_on_white_is_the_highest_contrast_there_is() =>
        Assert.Equal(21, ChipText.Contrast("#000000", "#FFFFFF"), precision: 6);

    [Fact]
    public async Task A_problem_from_the_api_keeps_its_kind_detail_and_extensions()
    {
        var server = new StubServer();
        server.OnProblem(HttpMethod.Get, "boards", 409, "wip-limit-reached", "Implement is at 5 of 5.",
            """, "violations": [{ "scope": "Step", "scopeName": "Implement", "limit": 5, "current": 5, "policy": "Strict" }] """);

        object? result = (await server.CreateApi().ListBoardsAsync(TestContext.Current.CancellationToken)).Value;

        var problem = Assert.IsType<ApiProblem>(result);
        Assert.Equal(("wip-limit-reached", 409, "Implement is at 5 of 5."), (problem.Kind, problem.Status, problem.Detail));
        Assert.Equal(new WipViolation(WipScope.Step, "Implement", 5, 5, WipPolicy.Strict), Assert.Single(problem.Violations));
    }

    [Fact]
    public async Task An_error_without_a_problem_body_still_becomes_a_problem()
    {
        object? result = (await new StubServer().CreateApi().ListBoardsAsync(TestContext.Current.CancellationToken)).Value;

        Assert.Equal(404, Assert.IsType<ApiProblem>(result).Status);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_reported_not_thrown()
    {
        var api = new KanbanApi(new ApiTransport(new HttpClient(new Unreachable()) { BaseAddress = new Uri("https://kanbanhub.test/") }));

        object? result = (await api.ListBoardsAsync(TestContext.Current.CancellationToken)).Value;

        Assert.Same(ApiProblem.Unreachable, result);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host.");
    }
}
