-- The book's task counts for a board at one moment (specification section 15.4):
-- estimated-but-not-started, in progress, and finished. A card that hasn't been broken down stands for its
-- estimated task count (1 if nobody estimated); a broken-down parent is represented by its tasks instead.
-- Canceled work is left out. @Bucket narrows the count to one priority bucket; NULL counts them all.
CREATE FUNCTION kb.tvf_TaskTotals
(
    @BoardId uniqueidentifier,
    @Bucket  tinyint,
    @AsOfUtc datetime2(3)
)
RETURNS TABLE
AS
RETURN
    SELECT PendingEstimated = ISNULL(SUM(CASE WHEN f.FirstStartedAtUtc IS NULL OR f.FirstStartedAtUtc > @AsOfUtc
                                              THEN ISNULL(c.EstimatedTasks, 1) END), 0),
           Active           = ISNULL(SUM(CASE WHEN f.FirstStartedAtUtc <= @AsOfUtc
                                               AND (f.FinishedAtUtc IS NULL OR f.FinishedAtUtc > @AsOfUtc) THEN 1 END), 0),
           Done             = ISNULL(SUM(CASE WHEN f.FinishedAtUtc <= @AsOfUtc THEN 1 END), 0)
    FROM kb.Card c
    JOIN kb.vCardFlow f ON f.CardId = c.CardId
    WHERE c.BoardId = @BoardId AND c.IsDeleted = 0 AND c.IsContainer = 0 AND c.[Location] <> 5
      AND c.CreatedAtUtc <= @AsOfUtc
      AND (@Bucket IS NULL OR c.PriorityBucket = @Bucket);
