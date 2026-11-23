CREATE TABLE kb.Board (
    BoardId               uniqueidentifier NOT NULL CONSTRAINT DF_Board_Id DEFAULT NEWSEQUENTIALID() CONSTRAINT PK_Board PRIMARY KEY,
    WorkspaceId           uniqueidentifier NOT NULL CONSTRAINT FK_Board_Workspace REFERENCES kb.Workspace(WorkspaceId),
    [Name]                nvarchar(200)    NOT NULL,
    TemplateKey           varchar(40)      NOT NULL,
    WorkflowVersion       int              NOT NULL CONSTRAINT DF_Board_WfVer DEFAULT 1,
    NextCardNumber        int              NOT NULL CONSTRAINT DF_Board_NextNo DEFAULT 1,
    ReadyListCap          smallint         NULL,
    ParkingReviewWeeks    tinyint          NOT NULL CONSTRAINT DF_Board_Park DEFAULT 3,
    ArchiveAfterDays      smallint         NOT NULL CONSTRAINT DF_Board_Archive DEFAULT 14,
    StandupTimeLocal      time(0)          NULL,
    TimeZoneId            varchar(64)      NOT NULL,
    GitHubClosePolicy     tinyint          NOT NULL CONSTRAINT DF_Board_Close DEFAULT 1,  -- 1 request completion, 2 trust GitHub
    AgentConcurrencyLimit tinyint          NOT NULL CONSTRAINT DF_Board_Agents DEFAULT 3,
    IsArchived            bit              NOT NULL CONSTRAINT DF_Board_Archived DEFAULT 0,
    CreatedAtUtc          datetime2(3)     NOT NULL,
    CreatedBy             uniqueidentifier NOT NULL,
    [RowVersion]          rowversion       NOT NULL,
    AiEnabled             bit              NOT NULL CONSTRAINT DF_Board_Ai DEFAULT 0,             -- per-board opt-in (section 11.2)
    AiMonthlyTokenBudget  int              NOT NULL CONSTRAINT DF_Board_AiBudget DEFAULT 2000000  -- soft stop once a month's usage reaches it
);
