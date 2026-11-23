CREATE TABLE kb.WipLimit (
    WipLimitId    uniqueidentifier NOT NULL CONSTRAINT PK_WipLimit PRIMARY KEY,
    BoardId       uniqueidentifier NOT NULL CONSTRAINT FK_Wip_Board REFERENCES kb.Board(BoardId),
    Scope         tinyint          NOT NULL CONSTRAINT CK_Wip_Scope CHECK (Scope BETWEEN 1 AND 5),  -- step, lane, cell, person, group
    StepId        uniqueidentifier NULL CONSTRAINT FK_Wip_Step REFERENCES kb.BoardStep(StepId),
    SwimlaneId    uniqueidentifier NULL CONSTRAINT FK_Wip_Lane REFERENCES kb.Swimlane(SwimlaneId),
    UserId        uniqueidentifier NULL,
    StepGroupId   uniqueidentifier NULL,
    MaxItems      smallint         NOT NULL CONSTRAINT CK_Wip_Max CHECK (MaxItems BETWEEN 1 AND 99),
    [Policy]      tinyint          NOT NULL CONSTRAINT CK_Wip_Policy CHECK ([Policy] BETWEEN 1 AND 3), -- strict, reason, warn
    ChangedAtUtc  datetime2(3)     NOT NULL,
    ChangedBy     uniqueidentifier NOT NULL,
    ChangeReason  nvarchar(300)    NULL
);
GO
CREATE INDEX IX_Wip_Step ON kb.WipLimit(StepId, Scope) INCLUDE (MaxItems, [Policy], SwimlaneId);
