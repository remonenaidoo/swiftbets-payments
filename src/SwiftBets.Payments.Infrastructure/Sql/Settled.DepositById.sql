SELECT CONCAT('dep_', LOWER(REPLACE(CONVERT(char(36), PaymentId), '-', ''))) AS Reference, Amount, Currency, ProviderReference
FROM payments.Deposits WHERE PaymentId = @Id AND Status = 3;
