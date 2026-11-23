-- Creates a workspace and makes its creator the workspace Owner (role 1), a role its boards inherit.
CREATE PROCEDURE kb.usp_CreateWorkspace
    @WorkspaceId uniqueidentifier,
    @Name        nvarchar(200),
    @ActorId     uniqueidentifier,
    @NowUtc      datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    INSERT kb.Workspace (WorkspaceId, [Name], CreatedAtUtc, CreatedBy)
    VALUES (@WorkspaceId, @Name, @NowUtc, @ActorId);

    INSERT kb.RoleAssignment (RoleAssignmentId, WorkspaceId, BoardId, PrincipalId, PrincipalType, RoleId, CreatedAtUtc, CreatedBy)
    VALUES (NEWID(), @WorkspaceId, NULL, @ActorId, 1, 1, @NowUtc, @ActorId);

    COMMIT;
END;
