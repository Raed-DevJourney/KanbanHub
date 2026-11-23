-- Writes the two records every applied move leaves: the movement row and its outbox event.
-- Runs inside the caller's transaction.
CREATE PROCEDURE kb.usp_RecordMovement
    @CardId         uniqueidentifier,
    @BoardId        uniqueidentifier,
    @FromStepId     uniqueidentifier,
    @FromLocation   tinyint,
    @ToStepId       uniqueidentifier,
    @ToLocation     tinyint,
    @ActorId        uniqueidentifier,
    @Source         tinyint,
    @OverrideReason nvarchar(500),
    @Reason         nvarchar(500),
    @ViolationsJson nvarchar(max),
    @IdempotencyKey uniqueidentifier,
    @NowUtc         datetime2(3),
    @MovementId     bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT kb.CardMovement (CardId, BoardId, FromStepId, FromLocation, ToStepId, ToLocation, MovedAtUtc,
                            ActorId, Source, OverrideReason, Reason, ViolationsJson, IdempotencyKey)
    VALUES (@CardId, @BoardId, @FromStepId, @FromLocation, @ToStepId, @ToLocation, @NowUtc,
            @ActorId, @Source, @OverrideReason, @Reason, @ViolationsJson, @IdempotencyKey);

    SET @MovementId = SCOPE_IDENTITY();

    DECLARE @Payload nvarchar(max) =
        (SELECT @CardId AS cardId, @FromStepId AS fromStepId, @FromLocation AS fromLocation,
                @ToStepId AS toStepId, @ToLocation AS toLocation, @MovementId AS movementId, @Source AS source
         FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    INSERT kb.Outbox (AggregateId, BoardId, EventType, Payload, CreatedAtUtc)
    VALUES (@CardId, @BoardId, 'card.moved', @Payload, @NowUtc);
END;
