using System.Net;
using KanbanHub.Contracts;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.Web.State;
using KanbanHub.Web.Tests.Infrastructure;
using static KanbanHub.Web.Tests.Infrastructure.SampleBoard;

namespace KanbanHub.Web.Tests;

/// <summary>The board on screen: previews from the shared rules, and optimistic moves that snap back when refused.</summary>
public sealed class BoardStoreTests : IDisposable
{
    private static readonly BoardSlot ImplementActive = new(Implement, Location.Active);

    private readonly StubServer server = new();
    private readonly Notices notices = new();
    private readonly BoardStore store;

    private readonly CardDto waiting = Card("Waiting", Specify, Location.Done);
    private readonly CardDto building = Card("Building", Implement, Location.Active);
    private readonly CardDto built = Card("Built", Implement, Location.Done);

    public BoardStoreTests() => store = new BoardStore(server.CreateApi(), notices);

    public void Dispose() => server.Dispose();

    [Fact]
    public async Task Loading_reads_the_board_and_what_the_user_may_do()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building);

        Assert.Equal("Flow", store.Board?.Name);
        Assert.True(store.Can(Permission.CardMove));
        Assert.False(store.Can(Permission.WipSet));
        Assert.Equal(1, store.WipCountOf(Implement));
        Assert.Equal([waiting], store.CardsAt(new BoardSlot(Specify, Location.Done)));
    }

    [Fact]
    public async Task A_board_that_cannot_be_read_reports_why()
    {
        server.OnGet($"boards/{Id}/me", new BoardAccessResponse([]));
        server.OnProblem(HttpMethod.Get, $"boards/{Id}", 403, "forbidden", "Missing permission: BoardRead.");

        await store.LoadAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal("Missing permission: BoardRead.", store.LoadProblem?.Detail);
        Assert.Null(store.Board);
    }

    [Fact]
    public async Task A_move_shows_at_once_and_is_sent_with_the_cards_etag_and_a_fresh_key()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building);
        server.On(HttpMethod.Post, $"cards/{waiting.Id}/move", HttpStatusCode.OK, MovedJson(waiting));
        bool shownBeforeTheServerAnswered = false;
        store.Changed += () => shownBeforeTheServerAnswered |= server.Requests.All(r => r.Method != HttpMethod.Post) && ImplementActive.Holds(store.Find(waiting.Id)!);

        MoveResult result = await store.MoveAsync(waiting, ImplementActive, reason: null, TestContext.Current.CancellationToken);

        SentRequest sent = server.Last(HttpMethod.Post);
        Assert.Equal(MoveResult.Moved, result);
        Assert.True(shownBeforeTheServerAnswered);
        Assert.Equal("\"AAAAAAAAAAE=\"", sent.IfMatch);
        Assert.True(Guid.TryParse(sent.IdempotencyKey, out _));
        Assert.Contains($"\"toStepId\":\"{Implement.Id}\"", sent.Body);
        Assert.Contains("\"toLocation\":\"Active\"", sent.Body);
        Assert.StartsWith("Moved Waiting to Implement Active.", notices.Announcement);
    }

    [Fact]
    public async Task A_refused_move_snaps_back_and_names_the_cards_to_help_finish()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building, built);
        server.OnProblem(HttpMethod.Post, $"cards/{waiting.Id}/move", 409, "wip-limit-reached",
            "Implement is at 2 of 2. Help finish a card in Implement before starting another.",
            $$""", "swarmCandidates": ["{{building.Id}}", "{{built.Id}}"] """);

        // The preview already says "full", but the server is the authority: ask anyway and show its answer.
        MoveResult result = await store.MoveAsync(waiting, ImplementActive, reason: null, TestContext.Current.CancellationToken);

        Assert.Equal(MoveResult.Rejected, result);
        Assert.Equal(Location.Done, store.Find(waiting.Id)!.Location);
        Notice toast = Assert.Single(notices.Toasts);
        Assert.Equal(NoticeKind.Problem, toast.Kind);
        Assert.StartsWith("Implement is at 2 of 2.", toast.Message);
        Assert.Equal(2, toast.Details.Count);
        Assert.Contains("Building", toast.Details[0]);
    }

    [Fact]
    public async Task A_step_that_takes_overrides_asks_for_a_reason_before_calling_the_server()
    {
        await LoadAsync(BuiltInRoles.FlowManager, WipPolicy.AllowWithReason, waiting, building, built);

        MoveResult result = await store.MoveAsync(waiting, ImplementActive, reason: null, TestContext.Current.CancellationToken);

        Assert.Equal(MoveResult.ReasonRequired, result);
        Assert.DoesNotContain(server.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_reason_travels_with_the_move()
    {
        await LoadAsync(BuiltInRoles.FlowManager, WipPolicy.AllowWithReason, waiting, building, built);
        server.On(HttpMethod.Post, $"cards/{waiting.Id}/move", HttpStatusCode.OK, MovedJson(waiting));

        await store.MoveAsync(waiting, ImplementActive, "Customer hotfix", TestContext.Current.CancellationToken);

        Assert.Contains("\"overrideReason\":\"Customer hotfix\"", server.Last(HttpMethod.Post).Body);
    }

    [Fact]
    public async Task The_server_asking_for_a_reason_puts_the_card_back_and_says_so()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building);
        server.OnProblem(HttpMethod.Post, $"cards/{waiting.Id}/move", 428, "override-reason-required", "This limit can be exceeded only with a reason.");

        MoveResult result = await store.MoveAsync(waiting, ImplementActive, reason: null, TestContext.Current.CancellationToken);

        Assert.Equal(MoveResult.ReasonRequired, result);
        Assert.Equal(Specify.Id, store.Find(waiting.Id)!.StepId);
        Assert.Empty(notices.Toasts);
    }

    [Fact]
    public async Task The_preview_follows_the_flow_rules()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building);

        Assert.True(store.Preview(waiting, ImplementActive).IsAllowed);
        Assert.False(store.Preview(waiting, new BoardSlot(Validate, Location.Active)).IsLegal);      // skips a step
        Assert.False(store.Preview(waiting, new BoardSlot(Specify, Location.Done)).IsLegal);         // already there
        Assert.True(store.Preview(building, new BoardSlot(Implement, Location.Track)).IsAllowed);
        Assert.True(store.Preview(building, BoardSlot.ParkingLot).IsAllowed);
        Assert.True(store.Preview(building, new BoardSlot(Specify, Location.Active)).NeedsReason);   // backward
    }

    [Fact]
    public async Task The_preview_closes_a_full_strict_step_but_not_finishing_inside_it()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building, built);

        Assert.True(store.Preview(waiting, ImplementActive).IsFull);
        Assert.True(store.Preview(building, new BoardSlot(Implement, Location.Done)).IsAllowed);
    }

    [Fact]
    public async Task An_allow_with_reason_step_is_open_only_to_people_who_may_override()
    {
        await LoadAsync(BuiltInRoles.Member, WipPolicy.AllowWithReason, waiting, building, built);
        Assert.True(store.Preview(waiting, ImplementActive).IsFull);

        await LoadAsync(BuiltInRoles.FlowManager, WipPolicy.AllowWithReason, waiting, building, built);
        Assert.Equal(new MovePreview(IsLegal: true, IsFull: false, NeedsReason: true), store.Preview(waiting, ImplementActive));
    }

    [Fact]
    public async Task A_change_heard_during_a_reload_leads_to_one_more_reload_not_a_parallel_one()
    {
        await LoadAsync(BuiltInRoles.Member, waiting);
        int snapshotsBefore = SnapshotRequests;
        Task? heardMeanwhile = null;
        store.Changed += () => heardMeanwhile ??= store.CatchUpAsync(TestContext.Current.CancellationToken);

        await store.CatchUpAsync(TestContext.Current.CancellationToken);

        Assert.True(heardMeanwhile!.IsCompletedSuccessfully);
        Assert.Equal(2, SnapshotRequests - snapshotsBefore);
    }

    [Fact]
    public async Task Wip_is_described_for_a_screen_reader()
    {
        await LoadAsync(BuiltInRoles.Member, waiting, building);

        Assert.Equal("WIP 1 of 2.", store.DescribeWip(ImplementActive));
        Assert.Equal("WIP 1, no limit.", store.DescribeWip(new BoardSlot(Specify, Location.Active)));
        Assert.Equal("", store.DescribeWip(BoardSlot.Backlog));
    }

    private int SnapshotRequests => server.Requests.Count(request => request.Method == HttpMethod.Get && request.Path == $"boards/{Id}");

    private Task LoadAsync(BoardRole role, params CardDto[] cards) => LoadAsync(role, WipPolicy.Strict, cards);

    private Task LoadAsync(BoardRole role, WipPolicy implementPolicy, params CardDto[] cards)
    {
        server.OnGet($"boards/{Id}/me", new BoardAccessResponse(role.Permissions.Names()));
        server.OnGet($"boards/{Id}", With(implementPolicy, cards));
        return store.LoadAsync(Id, TestContext.Current.CancellationToken);
    }

    private static string MovedJson(CardDto card) =>
        $$"""{ "cardId": "{{card.Id}}", "boardId": "{{Id}}", "movementId": 7, "rowVersion": "AAAAAAAAAAI=", "recorded": [] }""";
}
