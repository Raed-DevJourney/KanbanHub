using KanbanHub.Data.Cards;
using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.Infrastructure;

/// <summary>What a test may vary about a move; everything else gets a sensible default.</summary>
internal sealed record MoveOptions
{
    public string? OverrideReason { get; init; }

    public string? Reason { get; init; }

    public Guid IdempotencyKey { get; init; } = Guid.NewGuid();

    /// <summary>Defaults to the card's current row version, as a client that just loaded the board would send.</summary>
    public byte[]? ExpectedRowVersion { get; init; }

    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Gives each test its own quick-start board and moves cards through the real repository and procedure.</summary>
public abstract class MoveCardTestBase(KanbanDatabase database) : IAsyncLifetime
{
    private protected TestBoard Board { get; private set; } = null!;

    private protected BoardRecords Records { get; } = new(database.Connections);

    private protected CardRepository Repository { get; } = new(database.Connections);

    private protected ISqlConnectionFactory Connections => database.Connections;

    public async ValueTask InitializeAsync() => Board = await TestBoard.CreateAsync(database.Connections);

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private protected async Task<MoveOutcome> MoveAsync(Guid cardId, CardPosition to, MoveOptions? options = null)
    {
        options ??= new MoveOptions();
        var request = new MoveRequest
        {
            CardId = cardId,
            ExpectedRowVersion = options.ExpectedRowVersion ?? (await Records.CardAsync(cardId)).RowVersion,
            To = to,
            ActorId = TestBoard.Actor,
            Source = MoveSource.User,
            IdempotencyKey = options.IdempotencyKey,
            Now = options.Now,
            OverrideReason = options.OverrideReason,
            Reason = options.Reason
        };

        return await Repository.MoveAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Moves a card and fails the test unless the move was applied.</summary>
    private protected async Task<Moved> MustMoveAsync(Guid cardId, CardPosition to, MoveOptions? options = null) =>
        Assert.IsType<Moved>((await MoveAsync(cardId, to, options)).Value);
}
