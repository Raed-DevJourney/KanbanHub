-- Every card move, and the single source for every flow metric.
CREATE TABLE kb.CardMovement (
    MovementId     bigint IDENTITY  NOT NULL CONSTRAINT PK_CardMovement PRIMARY KEY,
    CardId         uniqueidentifier NOT NULL,
    BoardId        uniqueidentifier NOT NULL,
    FromStepId     uniqueidentifier NULL,
    FromLocation   tinyint          NOT NULL,
    ToStepId       uniqueidentifier NULL,
    ToLocation     tinyint          NOT NULL,
    MovedAtUtc     datetime2(3)     NOT NULL,
    ActorId        uniqueidentifier NOT NULL,
    Source         tinyint          NOT NULL,   -- 1 UI, 2 GitHub, 3 agent, 4 API, 5 automation
    OverrideReason nvarchar(500)    NULL,       -- set only when the move exceeded an "allow with reason" limit
    Reason         nvarchar(500)    NULL,       -- why the card left the normal flow: rework (FR-M02) or cancellation (FR-C10)
    ViolationsJson nvarchar(max)    NULL CONSTRAINT CK_Move_Json CHECK (ViolationsJson IS NULL OR ISJSON(ViolationsJson) = 1),
    IdempotencyKey uniqueidentifier NOT NULL CONSTRAINT UQ_Move_Idem UNIQUE
);
GO
CREATE INDEX IX_Move_BoardTime ON kb.CardMovement(BoardId, MovedAtUtc)
    INCLUDE (CardId, FromStepId, FromLocation, ToStepId, ToLocation);
GO
CREATE INDEX IX_Move_Card ON kb.CardMovement(CardId, MovedAtUtc);
