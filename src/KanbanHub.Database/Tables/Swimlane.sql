CREATE TABLE kb.Swimlane (
    SwimlaneId      uniqueidentifier NOT NULL CONSTRAINT PK_Swimlane PRIMARY KEY,
    BoardId         uniqueidentifier NOT NULL CONSTRAINT FK_Swimlane_Board REFERENCES kb.Board(BoardId),
    [Name]          nvarchar(100)    NOT NULL,
    SortOrder       smallint         NOT NULL,
    ClassOfService  tinyint          NOT NULL CONSTRAINT DF_Swimlane_Cos DEFAULT 3,  -- 1 expedite, 2 fixed date, 3 standard, 4 intangible
    HasOwnPipeline  bit              NOT NULL CONSTRAINT DF_Swimlane_Own DEFAULT 0
);
