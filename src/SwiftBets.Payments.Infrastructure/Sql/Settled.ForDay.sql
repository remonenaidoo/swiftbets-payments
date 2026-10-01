-- Our side of a reconciliation day: credited deposits in, paid withdrawals out.
SELECT CONCAT('dep_', LOWER(REPLACE(CONVERT(char(36), PaymentId), '-', ''))) AS Reference, Amount, Currency, ProviderReference
FROM payments.Deposits
WHERE Provider = @Provider AND Status = 3 AND CompletedAt >= @From AND CompletedAt < @To
UNION ALL
SELECT CONCAT('wd_', LOWER(REPLACE(CONVERT(char(36), WithdrawalId), '-', ''))), -Amount, Currency, ProviderReference
FROM payments.Withdrawals
WHERE Provider = @Provider AND Status = 5 AND CompletedAt >= @From AND CompletedAt < @To;
