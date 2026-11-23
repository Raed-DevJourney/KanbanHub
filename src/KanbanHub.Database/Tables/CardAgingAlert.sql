-- Cards whose aging alert was raised (section 11.1, feature 4): each card is alerted once, with the numbers
-- the alert was based on.
CREATE TABLE kb.CardAgingAlert (
    CardId       uniqueidentifier NOT NULL CONSTRAINT PK_CardAgingAlert PRIMARY KEY
                                           CONSTRAINT FK_CardAgingAlert_Card REFERENCES kb.Card(CardId),
    BoardId      uniqueidentifier NOT NULL,
    AgeDays      decimal(9,1)     NOT NULL,   -- days in flow when the alert was raised
    ExpectedDays decimal(9,1)     NOT NULL,   -- the 85th percentile of cycle time for the card's type at that moment
    AlertedAtUtc datetime2(3)     NOT NULL
);
