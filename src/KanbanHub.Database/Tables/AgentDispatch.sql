-- An agent session started from a card (section 10): which agent, on what, and how far it got.
CREATE TABLE kb.AgentDispatch (
    DispatchId        uniqueidentifier NOT NULL CONSTRAINT PK_AgentDispatch PRIMARY KEY,
    CardId            uniqueidentifier NOT NULL CONSTRAINT FK_AgentDispatch_Card REFERENCES kb.Card(CardId),
    BoardId           uniqueidentifier NOT NULL,
    Agent             varchar(40)      NOT NULL,   -- copilot
    TargetRepo        nvarchar(200)    NOT NULL,   -- owner/name
    BaseBranch        nvarchar(200)    NOT NULL,
    Model             nvarchar(100)    NULL,
    [State]           tinyint          NOT NULL,   -- 1 working, 2 in review, 3 merged, 4 closed unmerged, 5 failed to start, 6 ended by a person
    PullRequestNumber int              NULL,
    InstructionsHash  binary(32)       NOT NULL,   -- SHA-256 of what the agent was told, for the audit trail
    DispatchedBy      uniqueidentifier NOT NULL,
    DispatchedAtUtc   datetime2(3)     NOT NULL,
    EndedAtUtc        datetime2(3)     NULL
);
GO
-- A card has at most one open session; the board's open sessions are what its agent limit counts.
CREATE UNIQUE INDEX UX_AgentDispatch_OpenCard ON kb.AgentDispatch(CardId) WHERE [State] IN (1, 2);
GO
CREATE INDEX IX_AgentDispatch_Board ON kb.AgentDispatch(BoardId, [State]);
GO
CREATE INDEX IX_AgentDispatch_Card ON kb.AgentDispatch(CardId, DispatchedAtUtc DESC);
