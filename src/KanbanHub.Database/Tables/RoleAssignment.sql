CREATE TABLE kb.RoleAssignment (
    RoleAssignmentId uniqueidentifier NOT NULL CONSTRAINT PK_RoleAssignment PRIMARY KEY,
    WorkspaceId      uniqueidentifier NULL CONSTRAINT FK_RA_Workspace REFERENCES kb.Workspace(WorkspaceId),
    BoardId          uniqueidentifier NULL CONSTRAINT FK_RA_Board REFERENCES kb.Board(BoardId),
    PrincipalId      uniqueidentifier NOT NULL,   -- Entra object id of user, group or service principal
    PrincipalType    tinyint          NOT NULL,   -- 1 user, 2 group, 3 service principal
    RoleId           smallint         NOT NULL CONSTRAINT FK_RA_Role REFERENCES kb.RoleDefinition(RoleId),
    CreatedAtUtc     datetime2(3)     NOT NULL,
    CreatedBy        uniqueidentifier NOT NULL,
    CONSTRAINT CK_RA_Scope CHECK (WorkspaceId IS NOT NULL OR BoardId IS NOT NULL)
);
GO
CREATE INDEX IX_RA_Board ON kb.RoleAssignment(BoardId, PrincipalId) INCLUDE (RoleId);
GO
CREATE INDEX IX_RA_Workspace ON kb.RoleAssignment(WorkspaceId, PrincipalId) INCLUDE (RoleId) WHERE BoardId IS NULL;
