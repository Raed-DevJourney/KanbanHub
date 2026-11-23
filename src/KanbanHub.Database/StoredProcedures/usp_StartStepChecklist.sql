-- Gives a card the current done-rule checklist of the step it enters.
-- Each visit to a step starts unchecked: work that comes back must meet the rule again.
CREATE PROCEDURE kb.usp_StartStepChecklist
    @CardId   uniqueidentifier,
    @StepId   uniqueidentifier,
    @CardType tinyint
AS
BEGIN
    SET NOCOUNT ON;

    DELETE kb.CardChecklistItem
    WHERE CardId = @CardId AND StepId = @StepId;

    -- A rule for the card's own type wins over the step's general rule.
    DECLARE @DoneRuleId uniqueidentifier =
    (
        SELECT TOP (1) r.DoneRuleId
        FROM kb.DoneRule r
        WHERE r.StepId = @StepId AND r.IsCurrent = 1 AND (r.CardType = @CardType OR r.CardType IS NULL)
        ORDER BY CASE WHEN r.CardType IS NULL THEN 1 ELSE 0 END
    );

    INSERT kb.CardChecklistItem (ChecklistItemId, CardId, StepId, DoneRuleItemId, [Text], IsRequired, EvidenceKind, SortOrder)
    SELECT NEWID(), @CardId, @StepId, i.DoneRuleItemId, i.[Text], i.IsRequired, i.EvidenceKind, i.SortOrder
    FROM kb.DoneRuleItem i
    WHERE i.DoneRuleId = @DoneRuleId;
END;
