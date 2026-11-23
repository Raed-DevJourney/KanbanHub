-- Markdown comments on a card. Origin and GitHubCommentId let the GitHub sync tell its own echoes apart.
CREATE TABLE kb.CardComment (
    CommentId       uniqueidentifier NOT NULL CONSTRAINT PK_CardComment PRIMARY KEY,
    CardId          uniqueidentifier NOT NULL CONSTRAINT FK_Comment_Card REFERENCES kb.Card(CardId),
    Body            nvarchar(max)    NOT NULL,
    AuthorId        uniqueidentifier NOT NULL,
    Origin          tinyint          NOT NULL CONSTRAINT DF_Comment_Origin DEFAULT 1,  -- 1 KanbanHub, 2 GitHub
    GitHubCommentId bigint           NULL,
    ExternalAuthor  nvarchar(100)    NULL,   -- the GitHub login of whoever wrote it there
    CreatedAtUtc    datetime2(3)     NOT NULL
);
GO
CREATE INDEX IX_Comment_Card ON kb.CardComment(CardId, CreatedAtUtc);
GO
CREATE UNIQUE INDEX UX_Comment_GitHub ON kb.CardComment(GitHubCommentId) WHERE GitHubCommentId IS NOT NULL;
