-- A card's title and description as a vector, for the duplicate finder (section 11.1, feature 8).
CREATE TABLE kb.CardEmbedding (
    CardId       uniqueidentifier NOT NULL CONSTRAINT PK_CardEmbedding PRIMARY KEY CONSTRAINT FK_Emb_Card REFERENCES kb.Card(CardId),
    Embedding    vector(1536)     NOT NULL,   -- dimension must match the embedding deployment
    Model        varchar(100)     NOT NULL,
    UpdatedAtUtc datetime2(3)     NOT NULL,
    TextHash     binary(32)       NULL        -- SHA-256 of the text that was embedded: an unchanged card isn't embedded again
);
GO

-- Cards that look like the same work (section 11.1, feature 8): found by vector distance, shown on both cards
-- until someone says they are not duplicates.
CREATE TABLE kb.CardDuplicate (
    CardId            uniqueidentifier NOT NULL CONSTRAINT FK_CardDuplicate_Card REFERENCES kb.Card(CardId),
    OtherCardId       uniqueidentifier NOT NULL CONSTRAINT FK_CardDuplicate_Other REFERENCES kb.Card(CardId),
    SimilarityPercent tinyint          NOT NULL,
    [State]           tinyint          NOT NULL CONSTRAINT DF_CardDuplicate_State DEFAULT 1,   -- 1 open, 2 dismissed
    FoundAtUtc        datetime2(3)     NOT NULL,
    ResolvedBy        uniqueidentifier NULL,
    ResolvedAtUtc     datetime2(3)     NULL,
    CONSTRAINT PK_CardDuplicate PRIMARY KEY (CardId, OtherCardId)
);
GO
CREATE INDEX IX_CardDuplicate_Other ON kb.CardDuplicate(OtherCardId) INCLUDE ([State]);
