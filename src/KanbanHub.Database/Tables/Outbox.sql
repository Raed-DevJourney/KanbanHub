CREATE TABLE kb.Outbox (
    OutboxId        bigint IDENTITY  NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
    AggregateId     uniqueidentifier NOT NULL,
    BoardId         uniqueidentifier NOT NULL,
    EventType       varchar(60)      NOT NULL,
    Payload         nvarchar(max)    NOT NULL CONSTRAINT CK_Outbox_Json CHECK (ISJSON(Payload) = 1),
    CreatedAtUtc    datetime2(3)     NOT NULL,
    DispatchedAtUtc datetime2(3)     NULL
);
GO
CREATE INDEX IX_Outbox_Pending ON kb.Outbox(OutboxId) WHERE DispatchedAtUtc IS NULL;
