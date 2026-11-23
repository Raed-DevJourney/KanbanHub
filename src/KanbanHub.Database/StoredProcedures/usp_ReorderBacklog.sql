-- Arranges a board's backlog (FR-B04): the ordered Ready list and the priority buckets.
-- @ReadyJson: the whole Ready list, card ids in order. @BucketsJson: [{ "cardId", "bucket" }] for cards changing bucket.
-- Only cards still in the backlog are touched.
-- Returns ResultCode 0, or 5 when the list is longer than the board's Ready cap.
CREATE PROCEDURE kb.usp_ReorderBacklog
    @BoardId     uniqueidentifier,
    @ReadyJson   nvarchar(max),
    @BucketsJson nvarchar(max),
    @ActorId     uniqueidentifier,
    @NowUtc      datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Ready TABLE (CardId uniqueidentifier PRIMARY KEY, Position int);

    INSERT @Ready
    SELECT CAST([value] AS uniqueidentifier), CAST([key] AS int) + 1 FROM OPENJSON(@ReadyJson);

    IF (SELECT COUNT(*) FROM @Ready) > (SELECT ReadyListCap FROM kb.Board WHERE BoardId = @BoardId)
    BEGIN
        SELECT ResultCode = 5;
        RETURN;
    END;

    BEGIN TRANSACTION;

    UPDATE c
    SET PriorityBucket = b.bucket, ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
    FROM kb.Card c
    JOIN OPENJSON(@BucketsJson) WITH (cardId uniqueidentifier, bucket tinyint) b ON b.cardId = c.CardId
    WHERE c.BoardId = @BoardId AND c.[Location] = 0 AND c.IsDeleted = 0;

    UPDATE c
    SET IsReady = IIF(r.CardId IS NULL, 0, 1), [Rank] = COALESCE(r.Position, c.[Rank]),
        ModifiedAtUtc = @NowUtc, ModifiedBy = @ActorId
    FROM kb.Card c
    LEFT JOIN @Ready r ON r.CardId = c.CardId
    WHERE c.BoardId = @BoardId AND c.[Location] = 0 AND c.IsDeleted = 0
      AND (c.IsReady = 1 OR r.CardId IS NOT NULL);

    COMMIT;

    SELECT ResultCode = 0;
END;
