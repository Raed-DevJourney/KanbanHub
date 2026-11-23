-- What the AI noticed about a board (section 11): the insight as JSON, with the cards and numbers it cites.
-- An insight is open until a person dismisses or accepts it, or a newer one of its kind replaces it.
CREATE TABLE kb.AiInsight (
    InsightId     bigint IDENTITY  NOT NULL CONSTRAINT PK_AiInsight PRIMARY KEY,
    BoardId       uniqueidentifier NOT NULL CONSTRAINT FK_AiInsight_Board REFERENCES kb.Board(BoardId),
    Kind          tinyint          NOT NULL,   -- 2 standup brief, 3 bottleneck, 4 WIP advice (AiFeature)
    PayloadJson   nvarchar(max)    NOT NULL CONSTRAINT CK_AiInsight_Json CHECK (ISJSON(PayloadJson) = 1),
    [State]       tinyint          NOT NULL CONSTRAINT DF_AiInsight_State DEFAULT 1,   -- 1 open, 2 dismissed, 3 accepted, 4 superseded
    CreatedAtUtc  datetime2(3)     NOT NULL,
    ResolvedBy    uniqueidentifier NULL,       -- who dismissed or accepted it
    ResolvedAtUtc datetime2(3)     NULL
);
GO
CREATE INDEX IX_AiInsight_Board ON kb.AiInsight(BoardId, [State], Kind);
GO

-- Tokens a board spent per month and feature, for its budget (section 11.2) and the usage figures.
CREATE TABLE kb.AiUsage (
    BoardId    uniqueidentifier NOT NULL CONSTRAINT FK_AiUsage_Board REFERENCES kb.Board(BoardId),
    UsageMonth date             NOT NULL,      -- the first day of the month
    Feature    tinyint          NOT NULL,      -- AiFeature
    Tokens     bigint           NOT NULL,
    Calls      int              NOT NULL,
    CONSTRAINT PK_AiUsage PRIMARY KEY (BoardId, UsageMonth, Feature)
);
