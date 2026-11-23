-- Replaces a board's step limits (rules R7 and R8). Steps left out of the list lose their limit.
-- Lowering a limit below the current count is allowed: the step then takes no pull until it drains.
-- @LimitsJson: [{ "stepId", "maxItems", "policy" }]. Every change is audited with actor and reason.
-- Returns ResultCode 0, or 5 when a step is not an open step of the board.
CREATE PROCEDURE kb.usp_SetWipLimits
    @BoardId    uniqueidentifier,
    @LimitsJson nvarchar(max),
    @Reason     nvarchar(300),
    @ActorId    uniqueidentifier,
    @NowUtc     datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @New TABLE (StepId uniqueidentifier PRIMARY KEY, MaxItems smallint, [Policy] tinyint);

    INSERT @New
    SELECT stepId, maxItems, [policy]
    FROM OPENJSON(@LimitsJson) WITH (stepId uniqueidentifier, maxItems smallint, [policy] tinyint);

    IF EXISTS (SELECT 1 FROM @New n
               WHERE NOT EXISTS (SELECT 1 FROM kb.BoardStep s
                                 WHERE s.StepId = n.StepId AND s.BoardId = @BoardId AND s.IsRetired = 0))
    BEGIN
        SELECT ResultCode = 5;
        RETURN;
    END;

    BEGIN TRANSACTION;

    -- One audit row per step whose limit is set, changed or removed.
    INSERT kb.AuditEvent (BoardId, EventType, Field, OldValue, NewValue, Reason, ActorId, OccurredAtUtc)
    SELECT @BoardId, 'wip.limit', s.[Name],
           CAST(w.MaxItems AS nvarchar(5)) + N' policy ' + CAST(w.[Policy] AS nvarchar(3)),
           CAST(n.MaxItems AS nvarchar(5)) + N' policy ' + CAST(n.[Policy] AS nvarchar(3)),
           @Reason, @ActorId, @NowUtc
    FROM kb.BoardStep s
    LEFT JOIN kb.WipLimit w ON w.StepId = s.StepId AND w.Scope = 1 AND w.SwimlaneId IS NULL
    LEFT JOIN @New n ON n.StepId = s.StepId
    WHERE s.BoardId = @BoardId AND s.IsRetired = 0
      AND NOT EXISTS (SELECT w.MaxItems, w.[Policy] INTERSECT SELECT n.MaxItems, n.[Policy]);

    DELETE w
    FROM kb.WipLimit w
    WHERE w.BoardId = @BoardId AND w.Scope = 1 AND w.SwimlaneId IS NULL
      AND NOT EXISTS (SELECT 1 FROM @New n WHERE n.StepId = w.StepId);

    UPDATE w
    SET MaxItems = n.MaxItems, [Policy] = n.[Policy], ChangedAtUtc = @NowUtc, ChangedBy = @ActorId, ChangeReason = @Reason
    FROM kb.WipLimit w
    JOIN @New n ON n.StepId = w.StepId
    WHERE w.Scope = 1 AND w.SwimlaneId IS NULL AND (w.MaxItems <> n.MaxItems OR w.[Policy] <> n.[Policy]);

    INSERT kb.WipLimit (WipLimitId, BoardId, Scope, StepId, MaxItems, [Policy], ChangedAtUtc, ChangedBy, ChangeReason)
    SELECT NEWID(), @BoardId, 1, n.StepId, n.MaxItems, n.[Policy], @NowUtc, @ActorId, @Reason
    FROM @New n
    WHERE NOT EXISTS (SELECT 1 FROM kb.WipLimit w WHERE w.StepId = n.StepId AND w.Scope = 1 AND w.SwimlaneId IS NULL);

    COMMIT;

    SELECT ResultCode = 0;
END;
