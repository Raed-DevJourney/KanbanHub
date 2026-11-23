-- The flow view behind every metric: cycle time = FinishedAtUtc - FirstStartedAtUtc,
-- work item age = now - FirstStartedAtUtc for unfinished cards, throughput = FinishedAtUtc per day.
CREATE VIEW kb.vCardFlow AS
SELECT c.CardId, c.BoardId, c.CardType, c.ClassOfService, c.IsContainer,
       FirstStartedAtUtc = (SELECT MIN(m.MovedAtUtc) FROM kb.CardMovement m
                            WHERE m.CardId = c.CardId AND m.ToLocation = 1),
       FinishedAtUtc     = (SELECT MAX(m.MovedAtUtc) FROM kb.CardMovement m
                            JOIN kb.BoardStep s ON s.StepId = m.ToStepId
                            WHERE m.CardId = c.CardId AND m.ToLocation = 2 AND s.IsFinal = 1)
FROM kb.Card c
WHERE c.IsDeleted = 0;
