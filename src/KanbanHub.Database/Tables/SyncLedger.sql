CREATE TABLE kb.SyncLedger (
    LedgerId      bigint IDENTITY  NOT NULL CONSTRAINT PK_SyncLedger PRIMARY KEY,
    CardId        uniqueidentifier NOT NULL,
    Direction     tinyint          NOT NULL,  -- 1 outbound, 2 inbound
    Field         varchar(40)      NOT NULL,
    ValueHash     binary(32)       NOT NULL,  -- SHA-256 of the normalized value
    SourceUpdated datetime2(3)     NOT NULL,  -- GitHub updated_at or KanbanHub ModifiedAtUtc
    OccurredAtUtc datetime2(3)     NOT NULL
);
GO
CREATE INDEX IX_SyncLedger_CardField ON kb.SyncLedger(CardId, Field, OccurredAtUtc DESC) INCLUDE (Direction, ValueHash, SourceUpdated);
