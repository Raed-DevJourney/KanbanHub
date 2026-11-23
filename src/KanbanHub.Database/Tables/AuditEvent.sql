-- Who changed what, when and why: card fields and flags, WIP limits, done rules, members (FR-C09, rule R8).
-- Card moves are audited by kb.CardMovement.
CREATE TABLE kb.AuditEvent (
    AuditEventId  bigint IDENTITY  NOT NULL CONSTRAINT PK_AuditEvent PRIMARY KEY,
    BoardId       uniqueidentifier NOT NULL,
    CardId        uniqueidentifier NULL,       -- null for board-level changes
    EventType     varchar(40)      NOT NULL,   -- card.field, card.blocked, card.unblocked, wip.limit, done-rule, members, board.created
    Field         nvarchar(100)    NULL,       -- the card field or the step the change is about
    OldValue      nvarchar(max)    NULL,
    NewValue      nvarchar(max)    NULL,
    Reason        nvarchar(500)    NULL,
    ActorId       uniqueidentifier NOT NULL,
    OccurredAtUtc datetime2(3)     NOT NULL
);
GO
CREATE INDEX IX_Audit_Board ON kb.AuditEvent(BoardId, OccurredAtUtc DESC);
GO
CREATE INDEX IX_Audit_Card ON kb.AuditEvent(CardId, OccurredAtUtc DESC) WHERE CardId IS NOT NULL;
