CREATE TABLE kb.RoleDefinition (
    RoleId      smallint      NOT NULL CONSTRAINT PK_Role PRIMARY KEY,
    [Name]      nvarchar(100) NOT NULL CONSTRAINT UQ_Role_Name UNIQUE,
    IsBuiltIn   bit           NOT NULL,
    Permissions bigint        NOT NULL   -- KanbanHub.Domain.Security.Permission flags
);
