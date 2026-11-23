-- Creates a board from a template the domain has already resolved into steps.
-- @StepsJson: [{ "name", "color", "hasTrack", "countsParentsOnly", "isAgentEligible", "wipLimit" }] in workflow order;
-- the last step is the final one. The creator becomes the board's Owner (role 1).
-- Returns ResultCode 0, or 6 when the workspace doesn't exist.
CREATE PROCEDURE kb.usp_CreateBoard
    @BoardId     uniqueidentifier,
    @WorkspaceId uniqueidentifier,
    @Name        nvarchar(200),
    @TemplateKey varchar(40),
    @TimeZoneId  varchar(64),
    @StepsJson   nvarchar(max),
    @ActorId     uniqueidentifier,
    @NowUtc      datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM kb.Workspace WHERE WorkspaceId = @WorkspaceId)
    BEGIN
        SELECT ResultCode = 6;
        RETURN;
    END;

    DECLARE @Steps TABLE (StepId uniqueidentifier, SortOrder smallint, [Name] nvarchar(100), Color char(7),
                          HasTrack bit, CountsParentsOnly bit, IsAgentEligible bit, WipLimit smallint);

    INSERT @Steps
    SELECT NEWID(), CAST(j.[key] AS smallint) + 1, s.[name], s.color, s.hasTrack, s.countsParentsOnly, s.isAgentEligible, s.wipLimit
    FROM OPENJSON(@StepsJson) j
    CROSS APPLY OPENJSON(j.[value])
         WITH ([name] nvarchar(100), color char(7), hasTrack bit, countsParentsOnly bit, isAgentEligible bit, wipLimit smallint) s;

    BEGIN TRANSACTION;

    INSERT kb.Board (BoardId, WorkspaceId, [Name], TemplateKey, TimeZoneId, CreatedAtUtc, CreatedBy)
    VALUES (@BoardId, @WorkspaceId, @Name, @TemplateKey, @TimeZoneId, @NowUtc, @ActorId);

    INSERT kb.BoardStep (StepId, BoardId, [Name], SortOrder, Color, HasTrack, CountsParentsOnly, IsFinal, IsAgentEligible, WorkflowVersion)
    SELECT StepId, @BoardId, [Name], SortOrder, Color, HasTrack, CountsParentsOnly,
           IIF(SortOrder = (SELECT MAX(SortOrder) FROM @Steps), 1, 0), IsAgentEligible, 1
    FROM @Steps;

    INSERT kb.WipLimit (WipLimitId, BoardId, Scope, StepId, MaxItems, [Policy], ChangedAtUtc, ChangedBy, ChangeReason)
    SELECT NEWID(), @BoardId, 1, StepId, WipLimit, 1, @NowUtc, @ActorId, N'Template default'
    FROM @Steps
    WHERE WipLimit IS NOT NULL;

    INSERT kb.RoleAssignment (RoleAssignmentId, WorkspaceId, BoardId, PrincipalId, PrincipalType, RoleId, CreatedAtUtc, CreatedBy)
    VALUES (NEWID(), NULL, @BoardId, @ActorId, 1, 1, @NowUtc, @ActorId);

    INSERT kb.AuditEvent (BoardId, EventType, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, 'board.created', @TemplateKey, @ActorId, @NowUtc);

    COMMIT;

    SELECT ResultCode = 0;
END;
