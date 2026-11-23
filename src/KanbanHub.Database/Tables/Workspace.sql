CREATE TABLE kb.Workspace (
    WorkspaceId   uniqueidentifier NOT NULL CONSTRAINT DF_Workspace_Id DEFAULT NEWSEQUENTIALID() CONSTRAINT PK_Workspace PRIMARY KEY,
    [Name]        nvarchar(200)    NOT NULL,
    CreatedAtUtc  datetime2(3)     NOT NULL,
    CreatedBy     uniqueidentifier NOT NULL
);
