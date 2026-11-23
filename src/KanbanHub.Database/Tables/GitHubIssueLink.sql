CREATE TABLE kb.GitHubIssueLink (
    CardId            uniqueidentifier NOT NULL CONSTRAINT PK_IssueLink PRIMARY KEY CONSTRAINT FK_IssueLink_Card REFERENCES kb.Card(CardId),
    InstallationId    bigint           NOT NULL,
    RepositoryId      bigint           NOT NULL,
    IssueNumber       int              NOT NULL,
    IssueNodeId       varchar(100)     NOT NULL,
    StatusCommentId   bigint           NULL,
    LastInboundAtUtc  datetime2(3)     NULL,
    LastOutboundAtUtc datetime2(3)     NULL,
    LinkedAtUtc       datetime2(3)     NOT NULL,   -- comments written before this stay where they were written
    SyncProblem       nvarchar(400)    NULL,       -- why the last change asked for in GitHub was not applied (the "sync issue" badge)
    ClosedInGitHub    bit              NOT NULL CONSTRAINT DF_IssueLink_Closed DEFAULT 0,   -- closed there while the card is still in flow
    CONSTRAINT UQ_IssueLink_Issue UNIQUE (RepositoryId, IssueNumber)
);
