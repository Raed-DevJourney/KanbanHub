-- Moves a card, or refuses to, in one transaction (specification section 5).
-- Every move source (UI, GitHub, agents, API, automation) goes through here; nothing bypasses WIP (rule R11).
--
-- First result set: ResultCode plus the columns that outcome needs.
--   0 Moved            MovementId, BoardId, RowVersion        then: limits exceeded by warning or override
--   1 WipLimitReached                                         then: violations
--   2 ReasonRequired                                          then: violations
--   3 DoneRuleUnmet                                           then: unmet item texts
--   4 Conflict         CurrentRowVersion
--   5 IllegalTarget    (the step is not an open column of the card's board)
--   6 NotFound
CREATE PROCEDURE kb.usp_MoveCard
    @CardId             uniqueidentifier,
    @ExpectedRowVersion binary(8),
    @ToStepId           uniqueidentifier = NULL,
    @ToLocation         tinyint,
    @ToRank             decimal(19,9) = NULL,
    @ActorId            uniqueidentifier,
    @OverrideReason     nvarchar(500) = NULL,   -- non-null only if the caller holds wip.override
    @Reason             nvarchar(500) = NULL,   -- why the card leaves the normal flow: rework or cancellation
    @Source             tinyint,                -- 1 UI, 2 GitHub, 3 agent, 4 API, 5 automation
    @IdempotencyKey     uniqueidentifier,
    @NowUtc             datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Violations TABLE (Scope tinyint, ScopeName nvarchar(200), MaxItems int, CurrentCount int, [Policy] tinyint);

    BEGIN TRANSACTION;

    ---------------------------------------------------------------- lock 1: the card, parent first
    -- A family always locks top-down (parent, then task), so a task's move and its parent's never deadlock.
    DECLARE @ParentId uniqueidentifier = (SELECT ParentCardId FROM kb.Card WHERE CardId = @CardId);

    IF @ParentId IS NOT NULL
        SELECT @ParentId = CardId FROM kb.Card WITH (UPDLOCK, ROWLOCK) WHERE CardId = @ParentId;

    DECLARE @BoardId uniqueidentifier, @FromStepId uniqueidentifier, @FromLocation tinyint,
            @CardType tinyint, @IsContainer bit, @CurrentRv binary(8);

    SELECT @BoardId = BoardId, @FromStepId = StepId, @FromLocation = [Location],
           @CardType = CardType, @IsContainer = IsContainer, @CurrentRv = CAST([RowVersion] AS binary(8))
    FROM kb.Card WITH (UPDLOCK, ROWLOCK)
    WHERE CardId = @CardId AND IsDeleted = 0;

    IF @BoardId IS NULL
    BEGIN
        SELECT ResultCode = 6;
        ROLLBACK; RETURN;
    END;

    ---------------------------------------------------------------- idempotency (R10)
    -- Checked under the card lock, so a duplicate sent at the same time waits and then sees the original.
    DECLARE @ReplayedMovementId bigint, @ReplayedViolations nvarchar(max);

    SELECT @ReplayedMovementId = MovementId, @ReplayedViolations = ViolationsJson
    FROM kb.CardMovement
    WHERE IdempotencyKey = @IdempotencyKey AND CardId = @CardId;

    IF @ReplayedMovementId IS NOT NULL
    BEGIN
        SELECT ResultCode = 0, MovementId = @ReplayedMovementId, BoardId = @BoardId, [RowVersion] = @CurrentRv;
        SELECT Scope, ScopeName, MaxItems, CurrentCount, [Policy]
        FROM OPENJSON(@ReplayedViolations)
             WITH (Scope tinyint, ScopeName nvarchar(200), MaxItems int, CurrentCount int, [Policy] tinyint);
        COMMIT; RETURN;
    END;

    ---------------------------------------------------------------- gate: concurrency (R9)
    IF @CurrentRv <> @ExpectedRowVersion
    BEGIN
        SELECT ResultCode = 4, CurrentRowVersion = @CurrentRv;
        ROLLBACK; RETURN;
    END;

    ---------------------------------------------------------------- lock 2: the target step
    -- HOLDLOCK makes concurrent moves into one step take turns, so two can never both get the last slot.
    DECLARE @IsFinal bit, @HasTrack bit;

    IF @ToLocation IN (1, 2, 3)
    BEGIN
        SELECT @IsFinal = IsFinal, @HasTrack = HasTrack
        FROM kb.BoardStep WITH (UPDLOCK, HOLDLOCK)
        WHERE StepId = @ToStepId AND BoardId = @BoardId AND IsRetired = 0;

        IF @IsFinal IS NULL OR (@ToLocation = 3 AND @HasTrack = 0)
        BEGIN
            SELECT ResultCode = 5;
            ROLLBACK; RETURN;
        END;
    END
    ELSE
        SET @ToStepId = NULL;   -- backlog, parking lot, canceled and roll-up sit outside every step

    ---------------------------------------------------------------- gate: done rule
    -- Finishing a step (Active -> Done) needs every required checklist item ticked.
    IF @FromLocation = 1 AND @ToLocation = 2 AND @FromStepId = @ToStepId
       AND EXISTS (SELECT 1 FROM kb.CardChecklistItem
                   WHERE CardId = @CardId AND StepId = @ToStepId AND IsRequired = 1 AND IsChecked = 0)
    BEGIN
        SELECT ResultCode = 3;
        SELECT [Text]
        FROM kb.CardChecklistItem
        WHERE CardId = @CardId AND StepId = @ToStepId AND IsRequired = 1 AND IsChecked = 0
        ORDER BY SortOrder;
        ROLLBACK; RETURN;
    END;

    ---------------------------------------------------------------- gate: WIP
    -- Checked only when the move adds a counted card to the step. The final step's Done never counts (R1),
    -- so Active -> Done inside a step is never checked (R4) while Track -> Active is (R5).
    DECLARE @ToCounts   bit = IIF(@ToLocation = 1 OR (@ToLocation = 2 AND @IsFinal = 0), 1, 0);
    DECLARE @FromCounts bit = IIF(@FromStepId = @ToStepId
                                  AND (@FromLocation = 1 OR (@FromLocation = 2 AND @IsFinal = 0)), 1, 0);

    IF @ToCounts = 1 AND @FromCounts = 0
        INSERT @Violations (Scope, ScopeName, MaxItems, CurrentCount, [Policy])
        SELECT Scope, ScopeName, MaxItems, CurrentCount, [Policy]
        FROM kb.tvf_StepWipViolations(@ToStepId, @CardId, @ParentId);

    -- R6: strict rejects; "allow with reason" needs a reason; "warn" is only recorded.
    DECLARE @StrictHit  bit = IIF(EXISTS (SELECT 1 FROM @Violations WHERE [Policy] = 1), 1, 0);
    DECLARE @Overridden bit = IIF(EXISTS (SELECT 1 FROM @Violations WHERE [Policy] = 2), 1, 0);

    IF @StrictHit = 1 OR (@Overridden = 1 AND @OverrideReason IS NULL)
    BEGIN
        SELECT ResultCode = IIF(@StrictHit = 1, 1, 2);
        SELECT Scope, ScopeName, MaxItems, CurrentCount, [Policy] FROM @Violations;
        ROLLBACK; RETURN;
    END;

    ---------------------------------------------------------------- apply
    UPDATE kb.Card
    SET StepId               = @ToStepId,
        ParkedFromStepId     = CASE WHEN @ToLocation = 4 AND @FromLocation <> 4 THEN @FromStepId ELSE ParkedFromStepId END,
        CanceledReason       = CASE WHEN @ToLocation = 5 THEN @Reason ELSE CanceledReason END,
        [Location]           = @ToLocation,
        [Rank]               = COALESCE(@ToRank, [Rank]),
        EnteredLocationAtUtc = @NowUtc,
        ModifiedAtUtc        = @NowUtc,
        ModifiedBy           = @ActorId
    WHERE CardId = @CardId;

    DECLARE @NewRv binary(8) = (SELECT CAST([RowVersion] AS binary(8)) FROM kb.Card WHERE CardId = @CardId);

    -- Entering a step's Active starts that step's done-rule checklist; moves within the step keep it.
    IF @ToLocation = 1 AND (@FromStepId IS NULL OR @FromStepId <> @ToStepId)
        EXEC kb.usp_StartStepChecklist @CardId = @CardId, @StepId = @ToStepId, @CardType = @CardType;

    -- A parent that finishes the breakdown step takes its tasks along: they are one unit there (R2).
    IF @IsContainer = 1 AND @FromLocation = 1 AND @ToLocation = 2 AND @FromStepId = @ToStepId
        UPDATE kb.Card
        SET [Location] = 2, EnteredLocationAtUtc = @NowUtc, ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
        WHERE ParentCardId = @CardId AND StepId = @ToStepId AND [Location] = 1 AND IsDeleted = 0;

    DECLARE @MovementId bigint;
    DECLARE @RecordedOverride nvarchar(500) = IIF(@Overridden = 1, @OverrideReason, NULL);
    DECLARE @ViolationsJson nvarchar(max) =
        (SELECT Scope, ScopeName, MaxItems, CurrentCount, [Policy] FROM @Violations FOR JSON PATH);

    EXEC kb.usp_RecordMovement
        @CardId = @CardId, @BoardId = @BoardId, @FromStepId = @FromStepId, @FromLocation = @FromLocation,
        @ToStepId = @ToStepId, @ToLocation = @ToLocation, @ActorId = @ActorId, @Source = @Source,
        @OverrideReason = @RecordedOverride, @Reason = @Reason, @ViolationsJson = @ViolationsJson,
        @IdempotencyKey = @IdempotencyKey, @NowUtc = @NowUtc, @MovementId = @MovementId OUTPUT;

    IF @ParentId IS NOT NULL
        EXEC kb.usp_RollUpParent @ParentId = @ParentId, @ActorId = @ActorId, @NowUtc = @NowUtc;

    COMMIT;

    SELECT ResultCode = 0, MovementId = @MovementId, BoardId = @BoardId, [RowVersion] = @NewRv;
    SELECT Scope, ScopeName, MaxItems, CurrentCount, [Policy] FROM @Violations;
END;
