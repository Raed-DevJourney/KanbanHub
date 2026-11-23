-- A step's done rule, versioned. A rule for one card type overrides the step's general rule (CardType NULL).
CREATE TABLE kb.DoneRule (
    DoneRuleId   uniqueidentifier NOT NULL CONSTRAINT PK_DoneRule PRIMARY KEY,
    StepId       uniqueidentifier NOT NULL CONSTRAINT FK_DoneRule_Step REFERENCES kb.BoardStep(StepId),
    CardType     tinyint          NULL,
    [Version]    int              NOT NULL,
    IsCurrent    bit              NOT NULL CONSTRAINT DF_DoneRule_Current DEFAULT 1,
    CreatedAtUtc datetime2(3)     NOT NULL,
    CreatedBy    uniqueidentifier NOT NULL,
    CONSTRAINT UQ_DoneRule_Version UNIQUE (StepId, CardType, [Version])
);
GO
CREATE UNIQUE INDEX UX_DoneRule_Current ON kb.DoneRule(StepId, CardType) WHERE IsCurrent = 1;
