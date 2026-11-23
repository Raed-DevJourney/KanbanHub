-- A pull request that says it closes a linked issue, so its "Kanban done rule" check run can follow the card.
CREATE TABLE kb.GitHubPullRequest (
    RepositoryId bigint           NOT NULL,
    PullNumber   int              NOT NULL,
    CardId       uniqueidentifier NOT NULL CONSTRAINT FK_PullRequest_Card REFERENCES kb.Card(CardId),
    HeadSha      varchar(64)      NOT NULL,
    CheckRunId   bigint           NULL,
    [State]      tinyint          NOT NULL CONSTRAINT DF_PullRequest_State DEFAULT 1,   -- 1 open, 2 merged, 3 closed unmerged
    IsDraft      bit              NOT NULL CONSTRAINT DF_PullRequest_Draft DEFAULT 0,
    CONSTRAINT PK_GitHubPullRequest PRIMARY KEY (RepositoryId, PullNumber, CardId)
);
GO
CREATE INDEX IX_GitHubPullRequest_Card ON kb.GitHubPullRequest(CardId);
