-- One notification for one person. DedupeKey makes creating it idempotent: an event delivered twice notifies once.
CREATE TABLE kb.[Notification] (
    NotificationId  bigint IDENTITY  NOT NULL CONSTRAINT PK_Notification PRIMARY KEY,
    UserId          uniqueidentifier NOT NULL,
    BoardId         uniqueidentifier NOT NULL,
    CardId          uniqueidentifier NULL,
    Kind            varchar(40)      NOT NULL,   -- CardBlocked, CardUnblocked, WipOverrideUsed, CommentAdded, DueDate
    Title           nvarchar(400)    NOT NULL,
    Detail          nvarchar(500)    NULL,
    DedupeKey       varchar(100)     NOT NULL,
    ShowInInbox     bit              NOT NULL,   -- false when the person chose email only for this kind
    CreatedAtUtc    datetime2(3)     NOT NULL,
    ReadAtUtc       datetime2(3)     NULL,
    SnoozedUntilUtc datetime2(3)     NULL,
    CONSTRAINT UQ_Notification_Dedupe UNIQUE (UserId, DedupeKey)
);
GO
CREATE INDEX IX_Notification_Inbox ON kb.[Notification](UserId, CreatedAtUtc DESC) WHERE ShowInInbox = 1;
