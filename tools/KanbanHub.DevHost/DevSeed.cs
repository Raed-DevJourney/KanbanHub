using System.Security.Claims;
using KanbanHub.Contracts;
using KanbanHub.Data.Boards;
using KanbanHub.Data.Cards;
using KanbanHub.Domain.Boards;
using KanbanHub.Domain.Cards;
using KanbanHub.Domain.Flow;

namespace KanbanHub.DevHost;

/// <summary>
/// Creates one sample board, through the same use cases the API uses, the first time the dev host starts:
/// work in every column, a nearly full Implement step, a blocked card and a done rule to try things against.
/// </summary>
internal sealed class DevSeed(
    BoardCatalog catalog, BoardSettings settings, Backlog backlog, CardCommands commands, CardBlocking blocking,
    MoveCardHandler mover, BoardQueries boards, CardQueries cards)
{
    private static readonly ClaimsPrincipal Owner = DevPersona.Owner.ToPrincipal();

    private Guid boardId;
    private IReadOnlyList<StepDto> steps = [];

    public async Task EnsureSampleBoardAsync(CancellationToken cancellationToken)
    {
        if ((await boards.ListBoardsAsync(Owner, cancellationToken)).Count > 0) return;

        await CreateBoardAsync(cancellationToken);
        await AddMembersAndRulesAsync(cancellationToken);
        await AddCardsAsync(cancellationToken);
    }

    private async Task CreateBoardAsync(CancellationToken cancellationToken)
    {
        var workspace = (Workspace)(await catalog.CreateWorkspaceAsync("Demo team", Owner, cancellationToken)).Value!;
        var command = new CreateBoardCommand(workspace.Id, "Checkout redesign", BoardTemplates.QuickStart.Key, "UTC");
        boardId = ((NewBoard)(await catalog.CreateBoardAsync(command, Owner, cancellationToken)).Value!).Id;
        steps = (await boards.FindSnapshotAsync(boardId, cancellationToken))!.Steps;
    }

    private async Task AddMembersAndRulesAsync(CancellationToken cancellationToken)
    {
        MemberAssignment[] members =
        [
            .. DevPersona.All
                .Where(persona => persona.BoardRole is not null)
                .Select(persona => new MemberAssignment(persona.ObjectId, PrincipalType.User, persona.BoardRole!.Id))
        ];
        await settings.SetMembersAsync(boardId, members, Owner, cancellationToken);

        DoneRuleItemDraft[] rule = [new("Code reviewed", true), new("Unit tests pass", true), new("Docs updated", false)];
        await settings.SetDoneRuleAsync(new DoneRuleChange(boardId, steps[1].Id, CardType: null, rule), Owner, cancellationToken);
    }

    private async Task AddCardsAsync(CancellationToken cancellationToken)
    {
        // Finished and nearly finished work first, so the steps fill from the right as on a real board.
        await AddAsync(new CardContent { Title = "Guest checkout", PriorityBucket = 0, Origin = "U" }, 3, Location.Done, cancellationToken);
        await AddAsync(new CardContent { Title = "Address autocomplete", PriorityBucket = 0, Origin = "F" }, 3, Location.Active, cancellationToken);
        await AddAsync(new CardContent { Title = "Saved cards", PriorityBucket = 0, Origin = "TF" }, 2, Location.Done, cancellationToken);
        await AddAsync(new CardContent { Title = "Apple Pay button", PriorityBucket = 0, Origin = "L" }, 2, Location.Active, cancellationToken);
        await AddAsync(new CardContent { Title = "Retry policy for export job", CardType = CardType.Bug, PriorityBucket = 0, Origin = "B" }, 2, Location.Active, cancellationToken);
        Guid blocked = await AddAsync(new CardContent { Title = "Tax service integration", PriorityBucket = 1, Origin = "PF", DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)) }, 2, Location.Active, cancellationToken);
        await blocking.BlockAsync(blocked, new Blocker("Waiting for the partner's sandbox keys", BlockerCategory.External), Owner, cancellationToken);
        await AddAsync(new CardContent { Title = "Order summary redesign", CardType = CardType.Design, PriorityBucket = 1, Origin = "F" }, 1, Location.Done, cancellationToken);
        await AddAsync(new CardContent { Title = "Promo code field", PriorityBucket = 1, Origin = "S" }, 1, Location.Active, cancellationToken);

        string[] waiting = ["One-page checkout", "Gift receipts", "Checkout analytics events", "Remove legacy cart API", "Spike: passkeys at checkout"];
        foreach ((string title, int index) in waiting.Select((title, index) => (title, index)))
            await backlog.AddAsync(boardId, new CardContent { Title = title, PriorityBucket = (byte)Math.Min(index, 3), Origin = "U" }, Owner, cancellationToken);
    }

    /// <summary>Adds a card and walks it along the legal route to the given column of the 1-based step.</summary>
    private async Task<Guid> AddAsync(CardContent content, int stepOrder, Location location, CancellationToken cancellationToken)
    {
        var created = (CardCreated)(await backlog.AddAsync(boardId, content, Owner, cancellationToken)).Value!;
        foreach (StepDto step in steps.Where(step => step.Order <= stepOrder))
        {
            await MoveAsync(created.CardId, step, Location.Active, cancellationToken);
            if (step.Order < stepOrder || location is Location.Done)
            {
                await TickChecklistAsync(created.CardId, cancellationToken);
                await MoveAsync(created.CardId, step, Location.Done, cancellationToken);
            }
        }

        return created.CardId;
    }

    private async Task MoveAsync(Guid cardId, StepDto step, Location location, CancellationToken cancellationToken)
    {
        CardDetailDto card = (await cards.FindAsync(cardId, cancellationToken))!;
        var command = new MoveCardCommand
        {
            CardId = cardId,
            ExpectedRowVersion = Convert.FromBase64String(card.Card.ETag),
            ToStepId = step.Id,
            ToLocation = location,
            IdempotencyKey = Guid.NewGuid()
        };
        await mover.HandleAsync(command, Owner, cancellationToken);
    }

    private async Task TickChecklistAsync(Guid cardId, CancellationToken cancellationToken)
    {
        CardDetailDto card = (await cards.FindAsync(cardId, cancellationToken))!;
        foreach (ChecklistItemDto item in card.Checklist.Where(item => item is { IsRequired: true, IsChecked: false } && item.StepId == card.Card.StepId))
            await commands.ToggleChecklistItemAsync(cardId, item.Id, Owner, cancellationToken);
    }
}
