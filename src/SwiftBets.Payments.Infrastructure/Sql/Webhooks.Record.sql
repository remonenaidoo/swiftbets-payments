INSERT INTO payments.WebhookEvents (Provider, EventId, Kind, Reference, ReceivedAt)
SELECT @Provider, @EventId, @Kind, @Reference, @Now
WHERE NOT EXISTS (SELECT 1 FROM payments.WebhookEvents WHERE Provider = @Provider AND EventId = @EventId);
