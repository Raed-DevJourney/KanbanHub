CREATE TABLE kb.BoardStep (
    StepId            uniqueidentifier NOT NULL CONSTRAINT PK_BoardStep PRIMARY KEY,
    BoardId           uniqueidentifier NOT NULL CONSTRAINT FK_Step_Board REFERENCES kb.Board(BoardId),
    SwimlaneId        uniqueidentifier NULL CONSTRAINT FK_Step_Lane REFERENCES kb.Swimlane(SwimlaneId), -- set for lane pipelines
    [Name]            nvarchar(100)    NOT NULL,
    [Description]     nvarchar(1000)   NULL,
    SortOrder         smallint         NOT NULL,
    Color             char(7)          NOT NULL,
    HasTrack          bit              NOT NULL CONSTRAINT DF_Step_Track DEFAULT 0,
    CountsParentsOnly bit              NOT NULL CONSTRAINT DF_Step_Parents DEFAULT 0,
    IsFinal           bit              NOT NULL CONSTRAINT DF_Step_Final DEFAULT 0,
    IsAgentEligible   bit              NOT NULL CONSTRAINT DF_Step_Agent DEFAULT 0,
    WorkflowVersion   int              NOT NULL,
    IsRetired         bit              NOT NULL CONSTRAINT DF_Step_Retired DEFAULT 0
);
GO
CREATE UNIQUE INDEX UX_Step_Order ON kb.BoardStep(BoardId, SwimlaneId, SortOrder) WHERE IsRetired = 0;
