-- Stores a new version of a step's done rule; @CardType narrows it to one card type.
-- Cards already in the step keep the checklist they entered with.
-- @ItemsJson: [{ "text", "isRequired" }] in display order.
-- Returns ResultCode 0, or 5 when the step is not an open step of the board.
CREATE PROCEDURE kb.usp_SetDoneRule
    @BoardId   uniqueidentifier,
    @StepId    uniqueidentifier,
    @CardType  tinyint,
    @ItemsJson nvarchar(max),
    @ActorId   uniqueidentifier,
    @NowUtc    datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StepName nvarchar(100) =
        (SELECT [Name] FROM kb.BoardStep WHERE StepId = @StepId AND BoardId = @BoardId AND IsRetired = 0);

    IF @StepName IS NULL
    BEGIN
        SELECT ResultCode = 5;
        RETURN;
    END;

    BEGIN TRANSACTION;

    DECLARE @Version int = 1 + ISNULL((SELECT MAX([Version]) FROM kb.DoneRule WITH (UPDLOCK, HOLDLOCK)
                                       WHERE StepId = @StepId
                                         AND EXISTS (SELECT CardType INTERSECT SELECT @CardType)), 0);

    UPDATE kb.DoneRule
    SET IsCurrent = 0
    WHERE StepId = @StepId AND IsCurrent = 1 AND EXISTS (SELECT CardType INTERSECT SELECT @CardType);

    DECLARE @DoneRuleId uniqueidentifier = NEWID();

    INSERT kb.DoneRule (DoneRuleId, StepId, CardType, [Version], CreatedAtUtc, CreatedBy)
    VALUES (@DoneRuleId, @StepId, @CardType, @Version, @NowUtc, @ActorId);

    INSERT kb.DoneRuleItem (DoneRuleItemId, DoneRuleId, [Text], IsRequired, SortOrder)
    SELECT NEWID(), @DoneRuleId, i.[text], i.isRequired, CAST(j.[key] AS smallint)
    FROM OPENJSON(@ItemsJson) j
    CROSS APPLY OPENJSON(j.[value]) WITH ([text] nvarchar(300), isRequired bit) i;

    INSERT kb.AuditEvent (BoardId, EventType, Field, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, 'done-rule', @StepName, @ItemsJson, @ActorId, @NowUtc);

    COMMIT;

    SELECT ResultCode = 0;
END;
