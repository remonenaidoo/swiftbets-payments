-- Rolls back 0002_deposits_and_withdrawals. Refuses while any payment exists, since its money trail would be lost.
IF EXISTS (SELECT 1 FROM payments.Deposits) OR EXISTS (SELECT 1 FROM payments.Withdrawals)
    THROW 50001, 'Payments exist; they cannot be rolled back.', 1;
DROP TABLE IF EXISTS payments.WebhookEvents;
DROP TABLE IF EXISTS payments.Withdrawals;
DROP TABLE IF EXISTS payments.Deposits;
