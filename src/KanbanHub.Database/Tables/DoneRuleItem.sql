CREATE TABLE kb.DoneRuleItem (
    DoneRuleItemId uniqueidentifier NOT NULL CONSTRAINT PK_DoneRuleItem PRIMARY KEY,
    DoneRuleId     uniqueidentifier NOT NULL CONSTRAINT FK_DoneRuleItem_Rule REFERENCES kb.DoneRule(DoneRuleId),
    [Text]         nvarchar(300)    NOT NULL,
    IsRequired     bit              NOT NULL,
    EvidenceKind   tinyint          NOT NULL CONSTRAINT DF_DoneRuleItem_Ev DEFAULT 0, -- 0 manual, 1 PR approved, 2 checks passed, 3 PR merged
    SortOrder      smallint         NOT NULL
);
GO
CREATE INDEX IX_DoneRuleItem_Rule ON kb.DoneRuleItem(DoneRuleId, SortOrder);
