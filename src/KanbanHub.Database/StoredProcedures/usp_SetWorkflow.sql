-- Replaces a board's steps and bumps its workflow version (FR-B03, FR-B10).
-- @StepsJson, in workflow order: [{ "id" (null for a new step), "name", "description", "color",
--                                   "hasTrack", "countsParentsOnly", "isAgentEligible" }]. The last step is the final one.
-- Steps left out are retired, never deleted, so the movements that reference them keep every metric correct.
-- A step that still holds cards cannot be retired.
-- Returns ResultCode 0; 5 when an id is not an open step of the board; 6 when there is no such board;
--         7 with StepName when that step still holds cards.
CREATE PROCEDURE kb.usp_SetWorkflow
    @BoardId   uniqueidentifier,
    @StepsJson nvarchar(max),
    @ActorId   uniqueidentifier,
    @NowUtc    datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Steps TABLE (StepId uniqueidentifier NULL, SortOrder smallint, [Name] nvarchar(100), [Description] nvarchar(1000),
                          Color char(7), HasTrack bit, CountsParentsOnly bit, IsAgentEligible bit);

    INSERT @Steps
    SELECT s.id, CAST(j.[key] AS smallint) + 1, s.[name], s.[description], s.color, s.hasTrack, s.countsParentsOnly, s.isAgentEligible
    FROM OPENJSON(@StepsJson) j
    CROSS APPLY OPENJSON(j.[value])
         WITH (id uniqueidentifier, [name] nvarchar(100), [description] nvarchar(1000), color char(7),
               hasTrack bit, countsParentsOnly bit, isAgentEligible bit) s;

    BEGIN TRANSACTION;

    -- The board row is the lock that makes workflow edits take turns.
    DECLARE @Version int =
        (SELECT WorkflowVersion + 1 FROM kb.Board WITH (UPDLOCK, HOLDLOCK) WHERE BoardId = @BoardId AND IsArchived = 0);

    IF @Version IS NULL
    BEGIN
        SELECT ResultCode = 6;
        ROLLBACK; RETURN;
    END;

    IF EXISTS (SELECT 1 FROM @Steps n
               WHERE n.StepId IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM kb.BoardStep s
                                 WHERE s.StepId = n.StepId AND s.BoardId = @BoardId AND s.IsRetired = 0 AND s.SwimlaneId IS NULL))
    BEGIN
        SELECT ResultCode = 5;
        ROLLBACK; RETURN;
    END;

    DECLARE @Leaving TABLE (StepId uniqueidentifier PRIMARY KEY, [Name] nvarchar(100));

    INSERT @Leaving
    SELECT s.StepId, s.[Name]
    FROM kb.BoardStep s
    WHERE s.BoardId = @BoardId AND s.IsRetired = 0 AND s.SwimlaneId IS NULL
      AND NOT EXISTS (SELECT 1 FROM @Steps n WHERE n.StepId = s.StepId);

    DECLARE @Occupied nvarchar(100) =
        (SELECT TOP (1) l.[Name] FROM @Leaving l
         WHERE EXISTS (SELECT 1 FROM kb.Card c WHERE c.StepId = l.StepId AND c.IsDeleted = 0));

    IF @Occupied IS NOT NULL
    BEGIN
        SELECT ResultCode = 7, StepName = @Occupied;
        ROLLBACK; RETURN;
    END;

    UPDATE kb.BoardStep SET IsRetired = 1 WHERE StepId IN (SELECT StepId FROM @Leaving);
    DELETE kb.WipLimit WHERE StepId IN (SELECT StepId FROM @Leaving);

    DECLARE @Last smallint = (SELECT MAX(SortOrder) FROM @Steps);

    UPDATE s
    SET [Name] = n.[Name], [Description] = n.[Description], Color = n.Color, SortOrder = n.SortOrder, HasTrack = n.HasTrack,
        CountsParentsOnly = n.CountsParentsOnly, IsAgentEligible = n.IsAgentEligible, IsFinal = IIF(n.SortOrder = @Last, 1, 0)
    FROM kb.BoardStep s
    JOIN @Steps n ON n.StepId = s.StepId;

    INSERT kb.BoardStep (StepId, BoardId, [Name], [Description], SortOrder, Color, HasTrack, CountsParentsOnly, IsFinal,
                         IsAgentEligible, WorkflowVersion)
    SELECT NEWID(), @BoardId, n.[Name], n.[Description], n.SortOrder, n.Color, n.HasTrack, n.CountsParentsOnly,
           IIF(n.SortOrder = @Last, 1, 0), n.IsAgentEligible, @Version
    FROM @Steps n
    WHERE n.StepId IS NULL;

    UPDATE kb.Board SET WorkflowVersion = @Version WHERE BoardId = @BoardId;

    INSERT kb.AuditEvent (BoardId, EventType, Field, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, 'workflow', CONCAT(N'version ', @Version), @StepsJson, @ActorId, @NowUtc);

    COMMIT;

    SELECT ResultCode = 0;
END;
