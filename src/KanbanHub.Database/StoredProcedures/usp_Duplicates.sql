-- Stores a card's embedding. The vector arrives as a JSON array and is cast here, so no caller builds SQL from it.
CREATE PROCEDURE kb.usp_SaveCardEmbedding
    @CardId        uniqueidentifier,
    @EmbeddingJson nvarchar(max),
    @Model         varchar(100),
    @TextHash      binary(32),
    @NowUtc        datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Embedding vector(1536) = CAST(@EmbeddingJson AS vector(1536));

    BEGIN TRANSACTION;

    UPDATE kb.CardEmbedding WITH (UPDLOCK, HOLDLOCK)
    SET Embedding = @Embedding, Model = @Model, TextHash = @TextHash, UpdatedAtUtc = @NowUtc
    WHERE CardId = @CardId;

    IF @@ROWCOUNT = 0
        INSERT kb.CardEmbedding (CardId, Embedding, Model, TextHash, UpdatedAtUtc)
        VALUES (@CardId, @Embedding, @Model, @TextHash, @NowUtc);

    COMMIT;
END;
GO

-- The cards of the same board nearest to a card's embedding (cosine distance), nearest first, with what they are.
CREATE PROCEDURE kb.usp_FindNearestCards
    @CardId      uniqueidentifier,
    @MaxDistance float,
    @Take        int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Embedding vector(1536), @BoardId uniqueidentifier;
    SELECT @Embedding = e.Embedding, @BoardId = c.BoardId
    FROM kb.CardEmbedding e
    JOIN kb.Card c ON c.CardId = e.CardId
    WHERE e.CardId = @CardId;

    SELECT TOP (@Take) near.CardId, near.Distance
    FROM (
        SELECT o.CardId, Distance = VECTOR_DISTANCE('cosine', e.Embedding, @Embedding)
        FROM kb.CardEmbedding e
        JOIN kb.Card o ON o.CardId = e.CardId
        WHERE o.BoardId = @BoardId AND o.CardId <> @CardId AND o.IsDeleted = 0 AND o.[Location] <> 5
    ) near
    WHERE near.Distance <= @MaxDistance
    ORDER BY near.Distance, near.CardId;
END;
GO

-- Makes the given cards the open suggestions found from @CardId. A pair someone dismissed, in either direction,
-- stays dismissed; suggestions that no longer hold are withdrawn.
-- @DuplicatesJson: [{ "cardId", "similarityPercent" }]
CREATE PROCEDURE kb.usp_SaveCardDuplicates
    @CardId         uniqueidentifier,
    @DuplicatesJson nvarchar(max),
    @NowUtc         datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Found TABLE (OtherCardId uniqueidentifier PRIMARY KEY, SimilarityPercent tinyint);
    INSERT @Found
    SELECT cardId, similarityPercent
    FROM OPENJSON(@DuplicatesJson) WITH (cardId uniqueidentifier, similarityPercent tinyint);

    BEGIN TRANSACTION;

    DELETE d
    FROM kb.CardDuplicate d
    WHERE d.CardId = @CardId AND d.[State] = 1
      AND NOT EXISTS (SELECT 1 FROM @Found f WHERE f.OtherCardId = d.OtherCardId);

    UPDATE d
    SET SimilarityPercent = f.SimilarityPercent
    FROM kb.CardDuplicate d
    JOIN @Found f ON f.OtherCardId = d.OtherCardId
    WHERE d.CardId = @CardId AND d.[State] = 1;

    INSERT kb.CardDuplicate (CardId, OtherCardId, SimilarityPercent, [State], FoundAtUtc)
    SELECT @CardId, f.OtherCardId, f.SimilarityPercent, 1, @NowUtc
    FROM @Found f
    WHERE NOT EXISTS (SELECT 1 FROM kb.CardDuplicate d
                      WHERE (d.CardId = @CardId AND d.OtherCardId = f.OtherCardId)
                         OR (d.CardId = f.OtherCardId AND d.OtherCardId = @CardId));

    COMMIT;
END;
GO

-- Someone says two cards are not duplicates. The pair is dismissed whichever card it was found from.
-- Returns how many suggestions that closed.
CREATE PROCEDURE kb.usp_DismissCardDuplicate
    @CardId      uniqueidentifier,
    @OtherCardId uniqueidentifier,
    @ActorId     uniqueidentifier,
    @NowUtc      datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE kb.CardDuplicate
    SET [State] = 2, ResolvedBy = @ActorId, ResolvedAtUtc = @NowUtc
    WHERE [State] = 1
      AND ((CardId = @CardId AND OtherCardId = @OtherCardId) OR (CardId = @OtherCardId AND OtherCardId = @CardId));

    SELECT Dismissed = @@ROWCOUNT;
END;
