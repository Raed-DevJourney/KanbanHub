-- A repository linked to a board. A repository feeds one board, so a new issue in a mirrored repository has one place to go.
CREATE TABLE kb.GitHubRepoLink (
    RepositoryId   bigint           NOT NULL CONSTRAINT PK_GitHubRepoLink PRIMARY KEY,
    BoardId        uniqueidentifier NOT NULL CONSTRAINT FK_RepoLink_Board REFERENCES kb.Board(BoardId),
    InstallationId bigint           NOT NULL,
    FullName       nvarchar(200)    NOT NULL,   -- owner/name, kept current from repository events; shown and linked, never used to call GitHub
    SyncMode       tinyint          NOT NULL,   -- 1 link on demand, 2 mirror every open issue
    LabelFilter    nvarchar(100)    NULL,       -- mirror only issues that carry this label
    CreatedAtUtc   datetime2(3)     NOT NULL,
    CreatedBy      uniqueidentifier NOT NULL
);
GO
CREATE INDEX IX_GitHubRepoLink_Board ON kb.GitHubRepoLink(BoardId);
