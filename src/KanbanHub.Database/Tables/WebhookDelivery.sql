-- Webhook deliveries that were dealt with: the second layer of duplicate protection, and a trail of what arrived.
CREATE TABLE kb.WebhookDelivery (
    DeliveryId    varchar(100) NOT NULL CONSTRAINT PK_WebhookDelivery PRIMARY KEY,   -- X-GitHub-Delivery
    EventName     varchar(60)  NOT NULL,
    [Status]      tinyint      NOT NULL,   -- 1 handled, 2 ignored (nothing linked, an echo, or an event we don't act on)
    HandledAtUtc  datetime2(3) NOT NULL
);
