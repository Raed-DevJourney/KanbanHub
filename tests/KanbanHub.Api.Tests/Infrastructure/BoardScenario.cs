using System.Security.Claims;
using KanbanHub.Api.Endpoints;
using KanbanHub.Contracts;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Events;
using KanbanHub.Domain.Flow;
using KanbanHub.Domain.Security;
using KanbanHub.GitHub;
using KanbanHub.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KanbanHub.Api.Tests.Infrastructure;

/// <summary>
/// A quick-start board created through the API by its owner, plus the few moves tests keep repeating:
/// adding people with a role, adding cards, and walking a card to a column.
/// </summary>
internal sealed class BoardScenario
{
    private readonly KanbanDatabase database;
    private readonly ApiHarness api;
    private readonly IBoardEventPublisher publisher;
    private readonly HashSet<long> publishedEventIds = [];
    private IReadOnlyList<StepDto> steps = [];

    private BoardScenario(KanbanDatabase database, bool agentsEnabled)
    {
        this.database = database;
        api = new ApiHarness(database, agentsEnabled);
        Connection = api.Endpoints<GitHubConnectionEndpoints>();
        Agents = api.Endpoints<AgentDispatchEndpoints>();
        CardAi = api.Endpoints<CardAiEndpoints>();
        BoardAi = api.Endpoints<BoardAiEndpoints>();
        GitHubLinks = api.Endpoints<GitHubLinkEndpoints>();
        Webhook = api.Endpoints<GitHubWebhookEndpoint>();
        GitHub = api.Service<InMemoryGitHub>();
        Identity = api.Endpoints<IdentityEndpoints>();
        Boards = api.Endpoints<BoardEndpoints>();
        Settings = api.Endpoints<BoardSettingsEndpoints>();
        Backlog = api.Endpoints<BacklogEndpoints>();
        Cards = api.Endpoints<CardEndpoints>();
        Flow = api.Endpoints<CardFlowEndpoints>();
        Setup = api.Endpoints<BoardSetupEndpoints>();
        Insights = api.Endpoints<InsightEndpoints>();
        Comments = api.Endpoints<CommentEndpoints>();
        Inbox = api.Endpoints<InboxEndpoints>();
        Realtime = api.Endpoints<RealtimeEndpoints>();
        Gateway = api.Service<RecordingGateway>();
        Email = api.Service<RecordingEmail>();
        publisher = api.Service<IBoardEventPublisher>();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ClaimsPrincipal Owner { get; } = TestUsers.WithTenantRoles(TenantRoles.BoardCreator);

    public Guid WorkspaceId { get; private set; }

    public Guid BoardId { get; private set; }

    public StepDto Specify => steps[0];

    public StepDto Implement => steps[1];

    public StepDto Validate => steps[2];

    public IdentityEndpoints Identity { get; }

    public BoardEndpoints Boards { get; }

    public BoardSettingsEndpoints Settings { get; }

    public BacklogEndpoints Backlog { get; }

    public CardEndpoints Cards { get; }

    public CardFlowEndpoints Flow { get; }

    public BoardSetupEndpoints Setup { get; }

    public InsightEndpoints Insights { get; }

    public CommentEndpoints Comments { get; }

    public InboxEndpoints Inbox { get; }

    public RealtimeEndpoints Realtime { get; }

    public GitHubLinkEndpoints GitHubLinks { get; }

    public GitHubConnectionEndpoints Connection { get; }

    public AgentDispatchEndpoints Agents { get; }

    public CardAiEndpoints CardAi { get; }

    public BoardAiEndpoints BoardAi { get; }

    public GitHubWebhookEndpoint Webhook { get; }

    /// <summary>The stand-in for GitHub: what the sync wrote there, and where tests make "people on GitHub" act.</summary>
    public InMemoryGitHub GitHub { get; }

    /// <summary>Anything else the composition root built.</summary>
    public T Service<T>()
        where T : notnull => api.Service<T>();

    /// <summary>What was pushed to browsers.</summary>
    public RecordingGateway Gateway { get; }

    /// <summary>What was sent by email.</summary>
    public RecordingEmail Email { get; }

    /// <summary><paramref name="agentsEnabled"/> is the agent feature flag, as an environment would set it.</summary>
    public static async Task<BoardScenario> StartAsync(KanbanDatabase database, bool agentsEnabled = true)
    {
        var scenario = new BoardScenario(database, agentsEnabled);
        await scenario.CreateBoardAsync();
        return scenario;
    }

    /// <summary>A new signed-in user holding the given role on the board; no role at all when null.</summary>
    public async Task<ClaimsPrincipal> UserWithAsync(BoardRole? role)
    {
        ClaimsPrincipal user = TestUsers.WithTenantRoles();
        if (role is not null) await AssignAsync(user.GetObjectId(), PrincipalType.User, role);
        return user;
    }

    public Task AssignAsync(Guid principalId, PrincipalType type, BoardRole role) =>
        database.Connections.ExecuteAsync("""
            INSERT kb.RoleAssignment (RoleAssignmentId, BoardId, PrincipalId, PrincipalType, RoleId, CreatedAtUtc, CreatedBy)
            VALUES (NEWID(), @BoardId, @PrincipalId, @Type, @RoleId, SYSUTCDATETIME(), @PrincipalId);
            """,
            new { BoardId, PrincipalId = principalId, Type = (byte)type, RoleId = role.Id });

    public Task<Guid> AddCardAsync(ClaimsPrincipal? by = null, string title = "A card") =>
        AddCardAsync(new CardContent { Title = title }, by);

    public async Task<Guid> AddCardAsync(CardContent content, ClaimsPrincipal? by = null)
    {
        IActionResult result = await Backlog.CreateCard(Requests.From(by ?? Owner, content), BoardId, Ct);
        return result.ValueOf<CardCreatedDto>().Id;
    }

    /// <summary>Adds a card and walks it, as the owner, along the legal route to the given column.</summary>
    public async Task<Guid> AddCardAtAsync(StepDto target, Location location)
    {
        Guid cardId = await AddCardAsync();
        await WalkToAsync(cardId, target, location);
        return cardId;
    }

    /// <summary>Moves a card as a client would: with the card's current ETag and a fresh idempotency key.</summary>
    public async Task<IActionResult> MoveAsync(
        ClaimsPrincipal user, Guid cardId, StepDto? step, Location location, string? reason = null)
    {
        var body = new MoveCardBody(step?.Id, location, OverrideReason: reason);
        HttpRequest request = Requests.From(user, body, await ETagOfAsync(cardId), Guid.NewGuid());
        return await Flow.Move(request, cardId, Ct);
    }

    /// <summary>Walks an existing backlog card, as the owner, along the legal route to the given column.</summary>
    public async Task WalkToAsync(Guid cardId, StepDto target, Location location)
    {
        foreach (StepDto step in steps.TakeWhile(step => step.Order <= target.Order))
        {
            await MustMoveAsync(cardId, step, Location.Active);
            if (step.Order < target.Order || location is Location.Done) await MustMoveAsync(cardId, step, Location.Done);
        }

        if (location is Location.Track) await MustMoveAsync(cardId, target, Location.Track);
    }

    public async Task MustMoveAsync(Guid cardId, StepDto step, Location location) =>
        Assert.Equal(StatusCodes.Status200OK, (await MoveAsync(Owner, cardId, step, location)).StatusOf());

    public async Task<CardDetailDto> CardAsync(Guid cardId) =>
        (await Cards.Get(Requests.From(Owner), cardId, Ct)).ValueOf<CardDetailDto>();

    public async Task<string> ETagOfAsync(Guid cardId) => (await CardAsync(cardId)).Card.ETag;

    public async Task<BoardSnapshotDto> SnapshotAsync() =>
        (await Boards.GetBoard(Requests.From(Owner), BoardId, Ct)).ValueOf<BoardSnapshotDto>();

    /// <summary>Does what the host does after a request: hands the card's new events to their consumers.</summary>
    public async Task PublishEventsOfAsync(Guid cardId)
    {
        List<BoardEvent> events = await database.Connections.EventsOfCardAsync(cardId);
        foreach (BoardEvent fresh in events.Where(boardEvent => publishedEventIds.Add(boardEvent.Id)))
            await publisher.PublishAsync(fresh, Ct);
    }

    public async Task<InboxDto> InboxOfAsync(ClaimsPrincipal user) =>
        (await Inbox.Get(Requests.From(user), Ct)).ValueOf<InboxDto>();

    public Task<IActionResult> SetLimitsAsync(ClaimsPrincipal user, params StepLimit[] limits) =>
        Settings.SetWipLimits(Requests.From(user, new SetWipLimitsRequest(limits, "Test setup")), BoardId, Ct);

    private async Task CreateBoardAsync()
    {
        IActionResult workspace = await Boards.CreateWorkspace(Requests.From(Owner, new CreateWorkspaceRequest("Team")), Ct);
        WorkspaceId = workspace.ValueOf<WorkspaceDto>().Id;

        var request = new CreateBoardRequest(WorkspaceId, "Flow", "quick-start", "UTC");
        BoardId = (await Boards.CreateBoard(Requests.From(Owner, request), Ct)).ValueOf<BoardSummaryDto>().Id;
        steps = (await SnapshotAsync()).Steps;
    }
}
