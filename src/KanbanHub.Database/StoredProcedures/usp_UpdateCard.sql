-- Replaces a card's editable content if nobody changed the card since the caller read it.
-- Each field that actually changed gets an audit row (FR-C09).
-- Returns ResultCode 0 with RowVersion, 4 with CurrentRowVersion on a conflict, or 6 when the card doesn't exist.
CREATE PROCEDURE kb.usp_UpdateCard
    @CardId             uniqueidentifier,
    @ExpectedRowVersion binary(8),
    @Title              nvarchar(300),
    @Description        nvarchar(max),
    @CardType           tinyint,
    @PriorityBucket     tinyint,
    @Origin             varchar(3),
    @DueDate            date,
    @TargetRelease      nvarchar(100),
    @EstimatedTasks     smallint,
    @ActorId            uniqueidentifier,
    @NowUtc             datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @BoardId uniqueidentifier, @CurrentRv binary(8);

    SELECT @BoardId = BoardId, @CurrentRv = CAST([RowVersion] AS binary(8))
    FROM kb.Card WITH (UPDLOCK, ROWLOCK)
    WHERE CardId = @CardId AND IsDeleted = 0;

    IF @BoardId IS NULL
    BEGIN
        SELECT ResultCode = 6;
        ROLLBACK; RETURN;
    END;

    IF @CurrentRv <> @ExpectedRowVersion
    BEGIN
        SELECT ResultCode = 4, CurrentRowVersion = @CurrentRv;
        ROLLBACK; RETURN;
    END;

    INSERT kb.AuditEvent (BoardId, CardId, EventType, Field, OldValue, NewValue, ActorId, OccurredAtUtc)
    SELECT @BoardId, @CardId, 'card.field', change.Field, change.OldValue, change.NewValue, @ActorId, @NowUtc
    FROM kb.Card c
    CROSS APPLY (VALUES
        ('Title',          c.Title,                                   @Title),
        ('Description',    c.[Description],                           @Description),
        ('CardType',       CAST(c.CardType AS nvarchar(max)),         CAST(@CardType AS nvarchar(max))),
        ('PriorityBucket', CAST(c.PriorityBucket AS nvarchar(max)),   CAST(@PriorityBucket AS nvarchar(max))),
        ('Origin',         CAST(c.Origin AS nvarchar(max)),           CAST(@Origin AS nvarchar(max))),
        ('DueDate',        CONVERT(nvarchar(max), c.DueDate, 23),     CONVERT(nvarchar(max), @DueDate, 23)),
        ('TargetRelease',  c.TargetRelease,                           @TargetRelease),
        ('EstimatedTasks', CAST(c.EstimatedTasks AS nvarchar(max)),   CAST(@EstimatedTasks AS nvarchar(max)))
    ) change (Field, OldValue, NewValue)
    WHERE c.CardId = @CardId
      AND NOT EXISTS (SELECT change.OldValue INTERSECT SELECT change.NewValue);

    UPDATE kb.Card
    SET Title = @Title, [Description] = @Description, CardType = @CardType, PriorityBucket = @PriorityBucket,
        Origin = @Origin, DueDate = @DueDate, TargetRelease = @TargetRelease, EstimatedTasks = @EstimatedTasks,
        ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
    WHERE CardId = @CardId;

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    VALUES (@CardId, @BoardId, 'card.updated', (SELECT @CardId AS cardId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), @NowUtc);

    COMMIT;

    SELECT ResultCode = 0, [RowVersion] = (SELECT CAST([RowVersion] AS binary(8)) FROM kb.Card WHERE CardId = @CardId);
END;
