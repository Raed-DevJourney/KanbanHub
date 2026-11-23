-- Keeps a broken-down parent in step with its tasks (FR-C03). Called after one of its tasks moved,
-- inside that move's transaction and under the parent's lock.
--   * When the last task has left the breakdown step, the parent leaves the columns and becomes a roll-up.
--   * When every task is finished or canceled (and at least one finished), the roll-up completes.
CREATE PROCEDURE kb.usp_RollUpParent
    @ParentId uniqueidentifier,
    @ActorId  uniqueidentifier,
    @NowUtc   datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BoardId uniqueidentifier, @FromStepId uniqueidentifier, @FromLocation tinyint;

    SELECT @BoardId = BoardId, @FromStepId = StepId, @FromLocation = [Location]
    FROM kb.Card
    WHERE CardId = @ParentId AND IsContainer = 1 AND IsDeleted = 0;

    DECLARE @ToStepId uniqueidentifier, @ToLocation tinyint;

    IF @FromLocation IN (1, 2)
       AND NOT EXISTS (SELECT 1 FROM kb.Card WHERE ParentCardId = @ParentId AND StepId = @FromStepId AND IsDeleted = 0)
        SET @ToLocation = 6;   -- roll-up

    IF @FromLocation = 6
       AND NOT EXISTS (SELECT 1 FROM kb.Card c
                       LEFT JOIN kb.BoardStep s ON s.StepId = c.StepId
                       WHERE c.ParentCardId = @ParentId AND c.IsDeleted = 0
                         AND c.[Location] <> 5 AND NOT (c.[Location] = 2 AND s.IsFinal = 1))
        SELECT TOP (1) @ToLocation = 2, @ToStepId = c.StepId   -- the final step's Done, where its tasks finished
        FROM kb.Card c
        JOIN kb.BoardStep s ON s.StepId = c.StepId
        WHERE c.ParentCardId = @ParentId AND c.IsDeleted = 0 AND c.[Location] = 2 AND s.IsFinal = 1;

    IF @ToLocation IS NULL RETURN;

    UPDATE kb.Card
    SET StepId = @ToStepId, [Location] = @ToLocation, EnteredLocationAtUtc = @NowUtc, ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
    WHERE CardId = @ParentId;

    DECLARE @MovementId bigint, @Key uniqueidentifier = NEWID();
    EXEC kb.usp_RecordMovement
        @CardId = @ParentId, @BoardId = @BoardId, @FromStepId = @FromStepId, @FromLocation = @FromLocation,
        @ToStepId = @ToStepId, @ToLocation = @ToLocation, @ActorId = @ActorId, @Source = 5,   -- automation
        @OverrideReason = NULL, @Reason = NULL, @ViolationsJson = NULL, @IdempotencyKey = @Key,
        @NowUtc = @NowUtc, @MovementId = @MovementId OUTPUT;
END;
