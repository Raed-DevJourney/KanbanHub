CREATE TABLE kb.Card (
    CardId               uniqueidentifier NOT NULL CONSTRAINT DF_Card_Id DEFAULT NEWSEQUENTIALID() CONSTRAINT PK_Card PRIMARY KEY,
    BoardId              uniqueidentifier NOT NULL CONSTRAINT FK_Card_Board REFERENCES kb.Board(BoardId),
    CardNumber           int              NOT NULL,
    Title                nvarchar(300)    NOT NULL,
    [Description]        nvarchar(max)    NULL,
    CardType             tinyint          NOT NULL,
    PriorityBucket       tinyint          NULL CONSTRAINT CK_Card_Bucket CHECK (PriorityBucket BETWEEN 0 AND 3),
    Origin               varchar(3)       NULL CONSTRAINT CK_Card_Origin CHECK (Origin IN ('U','X','F','S','L','TF','PF','B')),
    ClassOfService       tinyint          NOT NULL CONSTRAINT DF_Card_Cos DEFAULT 3,
    StepId               uniqueidentifier NULL CONSTRAINT FK_Card_Step REFERENCES kb.BoardStep(StepId),
    [Location]           tinyint          NOT NULL CONSTRAINT CK_Card_Loc CHECK ([Location] BETWEEN 0 AND 6), -- backlog, active, done, track, parked, canceled, rollup
    SwimlaneId           uniqueidentifier NULL CONSTRAINT FK_Card_Lane REFERENCES kb.Swimlane(SwimlaneId),
    [Rank]               decimal(19,9)    NOT NULL,
    IsReady              bit              NOT NULL CONSTRAINT DF_Card_Ready DEFAULT 0,  -- on the ordered Ready list at the top of the backlog (FR-B04)
    ParentCardId         uniqueidentifier NULL CONSTRAINT FK_Card_Parent REFERENCES kb.Card(CardId),
    IsContainer          bit              NOT NULL CONSTRAINT DF_Card_Container DEFAULT 0,
    EstimatedTasks       smallint         NULL,
    ActualTasks          smallint         NULL,
    DueDate              date             NULL,
    TargetRelease        nvarchar(100)    NULL,
    IsBlocked            bit              NOT NULL CONSTRAINT DF_Card_Blocked DEFAULT 0,
    BlockedReason        nvarchar(500)    NULL,
    BlockerCategory      tinyint          NULL,
    BlockedAtUtc         datetime2(3)     NULL,
    ParkedFromStepId     uniqueidentifier NULL,
    EnteredLocationAtUtc datetime2(3)     NOT NULL,
    CanceledReason       nvarchar(500)    NULL,
    IsDeleted            bit              NOT NULL CONSTRAINT DF_Card_Deleted DEFAULT 0,
    CreatedAtUtc         datetime2(3)     NOT NULL,
    CreatedBy            uniqueidentifier NOT NULL,
    ModifiedAtUtc        datetime2(3)     NOT NULL,
    ModifiedBy           uniqueidentifier NOT NULL,
    [RowVersion]         rowversion       NOT NULL,
    CONSTRAINT UQ_Card_Number UNIQUE (BoardId, CardNumber)
);
GO
CREATE INDEX IX_Card_StepLocation ON kb.Card(StepId, [Location]) INCLUDE (ParentCardId, SwimlaneId) WHERE IsDeleted = 0;
GO
CREATE INDEX IX_Card_BoardRank ON kb.Card(BoardId, [Location], [Rank]) WHERE IsDeleted = 0;
GO
CREATE INDEX IX_Card_Parent ON kb.Card(ParentCardId) WHERE ParentCardId IS NOT NULL;
