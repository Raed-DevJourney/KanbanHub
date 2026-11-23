using System.Net;
using KanbanHub.Web.Realtime;
using KanbanHub.Web.Tests.Infrastructure;
using static KanbanHub.Web.Tests.Infrastructure.SampleBoard;

namespace KanbanHub.Web.Tests;

/// <summary>The rules around the real-time connection: when it opens, which board it follows, what happens when it drops.</summary>
public sealed class LiveUpdatesTests : IAsyncDisposable
{
    private const string Negotiate = "realtime/negotiate";

    private static readonly string Join = $"realtime/boards/{Id}/join";

    private readonly StubServer server = new();
    private readonly FakeConnections connections = new();
    private readonly AdjustableTime time = new(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc));
    private readonly LiveUpdates live;
    private readonly List<LiveState> states = [];
    private int boardChanges;
    private int inboxChanges;

    public LiveUpdatesTests()
    {
        server.On(HttpMethod.Post, Negotiate, HttpStatusCode.OK, """{ "url": "https://hub.test/board", "accessToken": "token-1" }""");
        server.On(HttpMethod.Post, Join, HttpStatusCode.NoContent);

        live = new LiveUpdates(server.CreateApi(), connections, time);
        live.StateChanged += () => states.Add(live.State);
        live.BoardChanged += () => boardChanges++;
        live.InboxChanged += () => inboxChanges++;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeConnection Connection => connections.Created[^1];

    private int NegotiateCalls => server.Requests.Count(request => request.Path == Negotiate);

    public async ValueTask DisposeAsync()
    {
        await live.DisposeAsync();
        server.Dispose();
    }

    [Fact]
    public async Task Watching_a_board_connects_and_follows_it_with_the_connections_id()
    {
        await live.WatchAsync(Id, Ct);

        Assert.Equal(LiveState.Connected, live.State);
        Assert.Equal("https://hub.test/board", Connection.Url);
        Assert.Equal((Join, """{"connectionId":"connection-1"}"""), (server.Last(HttpMethod.Post).Path, server.Last(HttpMethod.Post).Body));
    }

    [Fact]
    public async Task A_board_is_refetched_when_it_starts_being_watched_because_earlier_changes_were_not_pushed()
    {
        await live.WatchAsync(Id, Ct);
        await live.WatchAsync(Id, Ct);   // coming back to the board from another page

        Assert.Equal((2, 0), (boardChanges, inboxChanges));
    }

    [Fact]
    public async Task The_connection_is_opened_once_however_many_parts_of_the_page_ask()
    {
        await Task.WhenAll(live.ConnectAsync(Ct), live.WatchAsync(Id, Ct), live.ConnectAsync(Ct));

        Assert.Single(connections.Created);
    }

    [Fact]
    public async Task A_connect_uses_the_token_that_came_with_the_address_and_a_later_reconnect_asks_for_a_new_one()
    {
        await live.ConnectAsync(Ct);

        string? atConnect = await Connection.AccessToken();
        int askedAtConnect = NegotiateCalls;
        time.Advance(TimeSpan.FromMinutes(5));
        string? atReconnect = await Connection.AccessToken();

        Assert.Equal(("token-1", 1), (atConnect, askedAtConnect));
        Assert.Equal(("token-1", 2), (atReconnect, NegotiateCalls));
    }

    [Fact]
    public async Task Only_changes_to_the_board_on_screen_are_passed_on()
    {
        await live.WatchAsync(Id, Ct);
        boardChanges = 0;

        Connection.PushBoardChange(Guid.NewGuid());
        Connection.PushBoardChange(Id);
        Connection.PushInboxChange();

        Assert.Equal((1, 1), (boardChanges, inboxChanges));
    }

    [Fact]
    public async Task After_a_reconnect_the_board_is_followed_again_and_everything_is_refetched()
    {
        await live.WatchAsync(Id, Ct);
        boardChanges = 0;

        await Connection.DropAsync();
        LiveState whileAway = live.State;
        await Connection.ComeBackAsync("connection-2");

        Assert.Equal((LiveState.Reconnecting, LiveState.Connected), (whileAway, live.State));
        Assert.Equal("""{"connectionId":"connection-2"}""", server.Last(HttpMethod.Post).Body);
        Assert.Equal((1, 1), (boardChanges, inboxChanges));
    }

    [Fact]
    public async Task A_connection_that_closes_for_good_leaves_the_app_offline()
    {
        await live.WatchAsync(Id, Ct);

        await Connection.CloseAsync();

        Assert.Equal([LiveState.Connected, LiveState.Offline], states);
    }

    [Fact]
    public async Task Without_a_hub_the_app_stays_offline_and_follows_nothing()
    {
        connections.HubIsUp = false;

        await live.WatchAsync(Id, Ct);

        Assert.Equal(LiveState.Offline, live.State);
        Assert.DoesNotContain(server.Requests, request => request.Path == Join);
    }

    [Fact]
    public async Task Without_the_api_no_connection_is_attempted()
    {
        server.OnProblem(HttpMethod.Post, Negotiate, 503, "unavailable", "Try again later.");

        await live.WatchAsync(Id, Ct);

        Assert.Empty(connections.Created);
        Assert.Equal(LiveState.Offline, live.State);
    }

    [Fact]
    public async Task Reconnecting_by_hand_starts_a_new_connection_and_catches_up()
    {
        connections.HubIsUp = false;
        await live.WatchAsync(Id, Ct);
        FakeConnection failed = Connection;
        connections.HubIsUp = true;
        boardChanges = 0;

        await live.ReconnectAsync(Ct);

        Assert.True(failed.IsDisposed);
        Assert.Equal((2, LiveState.Connected), (connections.Created.Count, live.State));
        Assert.Equal(Join, server.Last(HttpMethod.Post).Path);
        Assert.Equal((1, 1), (boardChanges, inboxChanges));
    }
}
