using System.Security.Claims;
using KanbanHub.AI;
using KanbanHub.Contracts;
using KanbanHub.Domain.Ai;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;
using static KanbanHub.Api.Tests.Infrastructure.BoardScenario;

namespace KanbanHub.Api.Tests.Infrastructure;

/// <summary>
/// A board that opted in to AI, the stand-in model behind it, and what tests do with them: script what the
/// "model" answers, make cards look older, and run a background analysis now.
/// </summary>
internal sealed class AiScenario
{
    private readonly KanbanDatabase database;

    private AiScenario(KanbanDatabase database, BoardScenario board) => (this.database, Board) = (database, board);

    public BoardScenario Board { get; }

    /// <summary>What the model was asked, and the place to script its answers.</summary>
    public ScriptedAiModel Model => Board.Service<ScriptedAiModel>();

    public static async Task<AiScenario> StartAsync(KanbanDatabase database, bool optedIn = true)
    {
        var scenario = new AiScenario(database, await BoardScenario.StartAsync(database));
        if (optedIn) await scenario.SetAsync(new { aiEnabled = true });
        return scenario;
    }

    /// <summary>Changes the board's settings as its owner.</summary>
    public async Task SetAsync(object patch) =>
        Assert.Equal(204, (await Board.Setup.Edit(Requests.From(Board.Owner, patch), Board.BoardId, Ct)).StatusOf());

    public Task<Guid> AddCardAsync(string title, string? description = null) =>
        Board.AddCardAsync(new CardContent { Title = title, Description = description });

    /// <summary>A card walked into a column, with a title to recognize it by.</summary>
    public async Task<Guid> AddCardAtAsync(string title, StepDto step, Location location)
    {
        Guid card = await AddCardAsync(title);
        await Board.WalkToAsync(card, step, location);
        return card;
    }

    /// <summary>Makes a card look as if it had entered its column, and been blocked, that many days ago.</summary>
    public Task AgeAsync(Guid card, int days) => database.Connections.ExecuteAsync("""
        UPDATE kb.Card
        SET EnteredLocationAtUtc = DATEADD(DAY, -@Days, EnteredLocationAtUtc), BlockedAtUtc = DATEADD(DAY, -@Days, BlockedAtUtc)
        WHERE CardId = @CardId;
        """,
        new { CardId = card, Days = days });

    /// <summary>Makes a card look as if it had first been started that many days ago: its age in flow, or its cycle time once finished.</summary>
    public Task StartedAsync(Guid card, int daysAgo) => database.Connections.ExecuteAsync("""
        UPDATE kb.CardMovement
        SET MovedAtUtc = DATEADD(DAY, -@Days, MovedAtUtc)
        WHERE MovementId = (SELECT MIN(MovementId) FROM kb.CardMovement WHERE CardId = @CardId AND ToLocation = 1);
        """,
        new { CardId = card, Days = daysAgo });

    /// <summary>A finished card that took the given number of days from first start to done.</summary>
    public async Task FinishedInAsync(int days)
    {
        Guid card = await AddCardAtAsync($"Took {days} days", Board.Validate, Location.Done);
        await StartedAsync(card, days);
    }

    /// <summary>Runs a background analysis for the board now, as the queue would hand it to the workers.</summary>
    public Task RunAsync(AiFeature feature) =>
        Board.Service<IAiJobInbox>().EnqueueAsync(new AiJob(Board.BoardId, feature), Ct);

    public async Task<IReadOnlyList<InsightDto>> InsightsAsync(ClaimsPrincipal? user = null) =>
        (await Board.BoardAi.List(Requests.From(user ?? Board.Owner), Board.BoardId, Ct)).ValueOf<IReadOnlyList<InsightDto>>();

    public async Task<AiStatusDto> StatusAsync() =>
        (await Board.BoardAi.Status(Requests.From(Board.Owner), Board.BoardId, Ct)).ValueOf<AiStatusDto>();

    public async Task<int> NumberOfAsync(Guid card) => (await Board.CardAsync(card)).Card.Number;
}
