-- Adds a card to a board's backlog, numbering it from the board's counter.
-- Returns ResultCode 0 with CardNumber and RowVersion, or 6 when the board doesn't exist.
CREATE PROCEDURE kb.usp_CreateCard
    @CardId         uniqueidentifier,
    @BoardId        uniqueidentifier,
    @Title          nvarchar(300),
    @Description    nvarchar(max),
    @CardType       tinyint,
    @PriorityBucket tinyint,
    @Origin         varchar(3),
    @DueDate        date,
    @TargetRelease  nvarchar(100),
    @EstimatedTasks smallint,
    @ActorId        uniqueidentifier,
    @NowUtc         datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @Number int;

    UPDATE kb.Board
    SET @Number = NextCardNumber, NextCardNumber = NextCardNumber + 1
    WHERE BoardId = @BoardId AND IsArchived = 0;

    IF @Number IS NULL
    BEGIN
        SELECT ResultCode = 6;
        ROLLBACK; RETURN;
    END;

    INSERT kb.Card (CardId, BoardId, CardNumber, Title, [Description], CardType, PriorityBucket, Origin, [Location], [Rank],
                    DueDate, TargetRelease, EstimatedTasks, EnteredLocationAtUtc, CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy)
    VALUES (@CardId, @BoardId, @Number, @Title, @Description, @CardType, @PriorityBucket, @Origin, 0, @Number,
            @DueDate, @TargetRelease, @EstimatedTasks, @NowUtc, @NowUtc, @ActorId, @NowUtc, @ActorId);

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    VALUES (@CardId, @BoardId, 'card.created', (SELECT @CardId AS cardId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc);

    COMMIT;

    SELECT ResultCode = 0, CardNumber = @Number,
           [RowVersion] = (SELECT CAST([RowVersion] AS binary(8)) FROM kb.Card WHERE CardId = @CardId);
END;
