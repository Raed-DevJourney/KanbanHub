-- Breaks a card down into child tasks (FR-C03). The tasks sit beside their parent, in its step and sub-column,
-- where the family counts as one WIP unit (R2). @TitlesJson: ["first task", "second task"].
-- Returns the new card ids in the order given.
CREATE PROCEDURE kb.usp_BreakDownCard
    @ParentCardId uniqueidentifier,
    @TitlesJson   nvarchar(max),
    @ActorId      uniqueidentifier,
    @NowUtc       datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Children TABLE (CardId uniqueidentifier, Ordinal int, Title nvarchar(300));

    INSERT @Children
    SELECT NEWID(), CAST([key] AS int), [value] FROM OPENJSON(@TitlesJson);

    BEGIN TRANSACTION;

    DECLARE @BoardId uniqueidentifier, @StepId uniqueidentifier, @Location tinyint, @Bucket tinyint;

    SELECT @BoardId = BoardId, @StepId = StepId, @Location = [Location], @Bucket = PriorityBucket
    FROM kb.Card WITH (UPDLOCK, ROWLOCK)
    WHERE CardId = @ParentCardId AND IsDeleted = 0;

    DECLARE @FirstNumber int;

    UPDATE kb.Board
    SET @FirstNumber = NextCardNumber, NextCardNumber = NextCardNumber + (SELECT COUNT(*) FROM @Children)
    WHERE BoardId = @BoardId;

    INSERT kb.Card (CardId, BoardId, CardNumber, Title, CardType, PriorityBucket, StepId, [Location], [Rank], ParentCardId,
                    EnteredLocationAtUtc, CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy)
    SELECT CardId, @BoardId, @FirstNumber + Ordinal, Title, 2, @Bucket, @StepId, @Location, @FirstNumber + Ordinal, @ParentCardId,
           @NowUtc, @NowUtc, @ActorId, @NowUtc, @ActorId
    FROM @Children;

    UPDATE kb.Card
    SET IsContainer = 1,
        ActualTasks = (SELECT COUNT(*) FROM kb.Card WHERE ParentCardId = @ParentCardId AND IsDeleted = 0),
        ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
    WHERE CardId = @ParentCardId;

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    SELECT CardId, @BoardId, 'card.created', (SELECT CardId AS cardId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc
    FROM @Children;

    COMMIT;

    SELECT CardId FROM @Children ORDER BY Ordinal;
END;
