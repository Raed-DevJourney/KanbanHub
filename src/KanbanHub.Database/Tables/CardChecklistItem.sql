CREATE TABLE kb.CardChecklistItem (
    ChecklistItemId uniqueidentifier NOT NULL CONSTRAINT PK_Checklist PRIMARY KEY,
    CardId          uniqueidentifier NOT NULL CONSTRAINT FK_Checklist_Card REFERENCES kb.Card(CardId),
    StepId          uniqueidentifier NOT NULL CONSTRAINT FK_Checklist_Step REFERENCES kb.BoardStep(StepId),
    DoneRuleItemId  uniqueidentifier NULL,
    [Text]          nvarchar(300)    NOT NULL,
    IsRequired      bit              NOT NULL,
    EvidenceKind    tinyint          NOT NULL CONSTRAINT DF_Checklist_Ev DEFAULT 0, -- 0 manual, 1 PR approved, 2 checks passed, 3 PR merged
    IsChecked       bit              NOT NULL CONSTRAINT DF_Checklist_Checked DEFAULT 0,
    CheckedBy       uniqueidentifier NULL,
    CheckedAtUtc    datetime2(3)     NULL,
    EvidenceUrl     nvarchar(500)    NULL,
    SortOrder       smallint         NOT NULL
);
GO
CREATE INDEX IX_Checklist_CardStep ON kb.CardChecklistItem(CardId, StepId) INCLUDE (IsRequired, IsChecked);
