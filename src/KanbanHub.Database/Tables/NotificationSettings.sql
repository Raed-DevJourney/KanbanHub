-- A person's channel choice for one kind of notification; kinds without a row use the defaults of section 11.3.
CREATE TABLE kb.NotificationPreference (
    UserId uniqueidentifier NOT NULL,
    Kind   varchar(40)      NOT NULL,
    InApp  bit              NOT NULL,
    Email  bit              NOT NULL,
    CONSTRAINT PK_NotificationPreference PRIMARY KEY (UserId, Kind)
);
GO
-- "Mute this card": the person gets no more notifications about it.
CREATE TABLE kb.CardMute (
    UserId uniqueidentifier NOT NULL,
    CardId uniqueidentifier NOT NULL CONSTRAINT FK_CardMute_Card REFERENCES kb.Card(CardId),
    CONSTRAINT PK_CardMute PRIMARY KEY (UserId, CardId)
);
