-- Ticks or unticks one done-rule item of a card. Returns how many items were toggled (0 when there is no such item).
CREATE PROCEDURE kb.usp_ToggleChecklistItem
    @CardId  uniqueidentifier,
    @ItemId  uniqueidentifier,
    @ActorId uniqueidentifier,
    @NowUtc  datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    UPDATE kb.CardChecklistItem
    SET IsChecked    = IsChecked ^ 1,
        CheckedBy    = IIF(IsChecked = 0, @ActorId, NULL),
        CheckedAtUtc = IIF(IsChecked = 0, @NowUtc, NULL)
    WHERE ChecklistItemId = @ItemId AND CardId = @CardId;

    DECLARE @Toggled int = @@ROWCOUNT;

    -- Other people looking at the card see the tick too.
    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    SELECT @CardId, c.BoardId, 'card.updated', (SELECT @CardId AS cardId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc
    FROM kb.Card c
    WHERE c.CardId = @CardId AND @Toggled > 0;

    COMMIT;

    SELECT Toggled = @Toggled;
END;
