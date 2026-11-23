using KanbanHub.Domain.Flow;
using KanbanHub.TestSupport;

namespace KanbanHub.Data.IntegrationTests.Infrastructure;

/// <summary>
/// Arranges a quick-start board (Specify, Implement, Validate) straight in the tables.
/// Specify counts parents only, Implement offers Track, Validate is final.
/// </summary>
internal sealed class TestBoard
{
    public const byte Feature = 1;
    public const byte Bug = 3;

    public static readonly Guid Actor = Guid.NewGuid();

    private readonly ISqlConnectionFactory database;

    private TestBoard(ISqlConnectionFactory database) => this.database = database;

    public Guid Id { get; } = Guid.NewGuid();

    public TestStep Specify { get; } = new(Guid.NewGuid(), "Specify", Order: 1);

    public TestStep Implement { get; } = new(Guid.NewGuid(), "Implement", Order: 2);

    public TestStep Validate { get; } = new(Guid.NewGuid(), "Validate", Order: 3);

    public static async Task<TestBoard> CreateAsync(ISqlConnectionFactory database)
    {
        var board = new TestBoard(database);
        await board.InsertAsync();
        return board;
    }

    public Task SetLimitAsync(TestStep step, int maxItems, WipPolicy policy = WipPolicy.Strict) =>
        database.ExecuteAsync("""
            DELETE kb.WipLimit WHERE StepId = @StepId;
            INSERT kb.WipLimit (WipLimitId, BoardId, Scope, StepId, MaxItems, [Policy], ChangedAtUtc, ChangedBy)
            VALUES (NEWID(), @BoardId, 1, @StepId, @MaxItems, @Policy, SYSUTCDATETIME(), @Actor);
            """,
            new { BoardId = Id, StepId = step.Id, MaxItems = maxItems, Policy = (byte)policy, Actor });

    /// <summary>Sets the step's done rule; pass a card type to override the rule for that type only.</summary>
    public async Task SetDoneRuleAsync(TestStep step, DoneRuleItem[] items, byte? cardType = null)
    {
        Guid ruleId = Guid.NewGuid();
        await database.ExecuteAsync("""
            INSERT kb.DoneRule (DoneRuleId, StepId, CardType, [Version], CreatedAtUtc, CreatedBy)
            VALUES (@RuleId, @StepId, @CardType, 1, SYSUTCDATETIME(), @Actor);
            """,
            new { RuleId = ruleId, StepId = step.Id, CardType = cardType, Actor });

        for (int order = 0; order < items.Length; order++)
        {
            await database.ExecuteAsync("""
                INSERT kb.DoneRuleItem (DoneRuleItemId, DoneRuleId, [Text], IsRequired, SortOrder)
                VALUES (NEWID(), @RuleId, @Text, @IsRequired, @SortOrder);
                """,
                new { RuleId = ruleId, items[order].Text, items[order].IsRequired, SortOrder = order });
        }
    }

    public async Task<Guid> AddCardAsync(CardPosition at, Guid? parentId = null, byte cardType = Feature)
    {
        Guid cardId = Guid.NewGuid();
        await database.ExecuteAsync("""
            DECLARE @Number int;
            UPDATE kb.Board SET @Number = NextCardNumber, NextCardNumber = NextCardNumber + 1 WHERE BoardId = @BoardId;

            INSERT kb.Card (CardId, BoardId, CardNumber, Title, CardType, StepId, [Location], [Rank], ParentCardId,
                            EnteredLocationAtUtc, CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy)
            VALUES (@CardId, @BoardId, @Number, CONCAT(N'Card ', @Number), @CardType, @StepId, @Location, @Number, @ParentId,
                    SYSUTCDATETIME(), SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor);
            """,
            new { CardId = cardId, BoardId = Id, CardType = cardType, at.StepId, Location = (byte)at.Location, ParentId = parentId, Actor });
        return cardId;
    }

    public async Task<Guid[]> AddCardsAsync(int count, CardPosition at, Guid? parentId = null)
    {
        var cardIds = new Guid[count];
        for (int i = 0; i < count; i++) cardIds[i] = await AddCardAsync(at, parentId);
        return cardIds;
    }

    public Task TickAsync(Guid cardId, string itemText) =>
        database.ExecuteAsync("""
            UPDATE kb.CardChecklistItem
            SET IsChecked = 1, CheckedBy = @Actor, CheckedAtUtc = SYSUTCDATETIME()
            WHERE CardId = @CardId AND [Text] = @Text;
            """,
            new { CardId = cardId, Text = itemText, Actor });

    public Task BlockAsync(Guid cardId) =>
        database.ExecuteAsync(
            "UPDATE kb.Card SET IsBlocked = 1, BlockedReason = N'Waiting', BlockedAtUtc = SYSUTCDATETIME() WHERE CardId = @CardId;",
            new { CardId = cardId });

    private Task InsertAsync() =>
        database.ExecuteAsync("""
            INSERT kb.Workspace (WorkspaceId, [Name], CreatedAtUtc, CreatedBy)
            VALUES (@WorkspaceId, N'Test workspace', SYSUTCDATETIME(), @Actor);

            INSERT kb.Board (BoardId, WorkspaceId, [Name], TemplateKey, TimeZoneId, CreatedAtUtc, CreatedBy)
            VALUES (@BoardId, @WorkspaceId, N'Quick start', 'quick-start', 'UTC', SYSUTCDATETIME(), @Actor);

            INSERT kb.BoardStep (StepId, BoardId, [Name], SortOrder, Color, HasTrack, CountsParentsOnly, IsFinal, WorkflowVersion)
            VALUES (@Specify,   @BoardId, N'Specify',   1, '#7C3AED', 0, 1, 0, 1),
                   (@Implement, @BoardId, N'Implement', 2, '#2563EB', 1, 0, 0, 1),
                   (@Validate,  @BoardId, N'Validate',  3, '#0D9488', 0, 0, 1, 1);
            """,
            new { WorkspaceId = Guid.NewGuid(), BoardId = Id, Specify = Specify.Id, Implement = Implement.Id, Validate = Validate.Id, Actor });
}

internal sealed record DoneRuleItem(string Text, bool IsRequired = true);
