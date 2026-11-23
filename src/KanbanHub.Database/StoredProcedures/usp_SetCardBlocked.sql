-- Sets or clears a card's blocked flag, with the reason and category when blocking. Returns the new RowVersion.
CREATE PROCEDURE kb.usp_SetCardBlocked
    @CardId    uniqueidentifier,
    @IsBlocked bit,
    @Reason    nvarchar(500),
    @Category  tinyint,
    @ActorId   uniqueidentifier,
    @NowUtc    datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @BoardId uniqueidentifier = (SELECT BoardId FROM kb.Card WITH (UPDLOCK, ROWLOCK) WHERE CardId = @CardId);
    DECLARE @EventType varchar(40) = IIF(@IsBlocked = 1, 'card.blocked', 'card.unblocked');

    UPDATE kb.Card
    SET IsBlocked = @IsBlocked,
        BlockedReason   = IIF(@IsBlocked = 1, @Reason, NULL),
        BlockerCategory = IIF(@IsBlocked = 1, @Category, NULL),
        BlockedAtUtc    = IIF(@IsBlocked = 1, @NowUtc, NULL),
        ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
    WHERE CardId = @CardId;

    INSERT kb.AuditEvent (BoardId, CardId, EventType, Reason, ActorId, OccurredAtUtc)
    VALUES (@BoardId, @CardId, @EventType, @Reason, @ActorId, @NowUtc);

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    VALUES (@CardId, @BoardId, @EventType, (SELECT @CardId AS cardId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc);

    COMMIT;

    SELECT [RowVersion] = CAST([RowVersion] AS binary(8)) FROM kb.Card WHERE CardId = @CardId;
END;
