-- Replaces the board's own role assignments. Roles inherited from the workspace are untouched.
-- @MembersJson: [{ "principalId", "principalType", "roleId" }].
CREATE PROCEDURE kb.usp_SetBoardMembers
    @BoardId     uniqueidentifier,
    @MembersJson nvarchar(max),
    @ActorId     uniqueidentifier,
    @NowUtc      datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DELETE kb.RoleAssignment WHERE BoardId = @BoardId;

    INSERT kb.RoleAssignment (RoleAssignmentId, WorkspaceId, BoardId, PrincipalId, PrincipalType, RoleId, CreatedAtUtc, CreatedBy)
    SELECT NEWID(), NULL, @BoardId, m.principalId, m.principalType, m.roleId, @NowUtc, @ActorId
    FROM OPENJSON(@MembersJson) WITH (principalId uniqueidentifier, principalType tinyint, roleId smallint) m;

    INSERT kb.AuditEvent (BoardId, EventType, NewValue, ActorId, OccurredAtUtc)
    VALUES (@BoardId, 'members', @MembersJson, @ActorId, @NowUtc);

    COMMIT;
END;
