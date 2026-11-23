-- The step limits that pulling @CardId into the step would exceed (rules R1, R2 and R7).
-- Returns no row when the card fits. The caller must hold the step's update lock (see kb.usp_MoveCard).
CREATE FUNCTION kb.tvf_StepWipViolations
(
    @StepId       uniqueidentifier,
    @CardId       uniqueidentifier,   -- the card being pulled; never counted among the occupants
    @ParentCardId uniqueidentifier    -- its parent, if it is a child task
)
RETURNS TABLE
AS
RETURN
    WITH Step AS
    (
        SELECT s.StepId, s.[Name], s.IsFinal, s.CountsParentsOnly
        FROM kb.BoardStep s
        WHERE s.StepId = @StepId
    ),
    Occupant AS
    (
        -- R1: Active always counts; Done counts unless the step is final; nothing else does.
        -- R2: where parents only count, a parent and its children are one unit.
        SELECT UnitId = CASE WHEN s.CountsParentsOnly = 1 THEN COALESCE(c.ParentCardId, c.CardId) ELSE c.CardId END
        FROM kb.Card c
        JOIN Step s ON s.StepId = c.StepId
        WHERE c.IsDeleted = 0
          AND c.CardId <> @CardId
          AND (c.[Location] = 1 OR (c.[Location] = 2 AND s.IsFinal = 0))
    ),
    StepLoad AS
    (
        SELECT s.[Name],
               CurrentCount = (SELECT COUNT(DISTINCT o.UnitId) FROM Occupant o),
               -- A card that joins its own family adds no unit.
               AddsUnit = CASE WHEN EXISTS (SELECT 1 FROM Occupant o
                                            WHERE o.UnitId = CASE WHEN s.CountsParentsOnly = 1
                                                                  THEN COALESCE(@ParentCardId, @CardId)
                                                                  ELSE @CardId END)
                               THEN 0 ELSE 1 END
        FROM Step s
    )
    SELECT Scope        = CAST(1 AS tinyint),          -- step
           ScopeName    = CAST(l.[Name] AS nvarchar(200)),
           MaxItems     = CAST(w.MaxItems AS int),
           CurrentCount = l.CurrentCount,
           w.[Policy]
    FROM StepLoad l
    JOIN kb.WipLimit w ON w.Scope = 1 AND w.StepId = @StepId AND w.SwimlaneId IS NULL
    WHERE l.CurrentCount + l.AddsUnit > w.MaxItems;   -- R7: a lowered limit takes no pull until the step drains
