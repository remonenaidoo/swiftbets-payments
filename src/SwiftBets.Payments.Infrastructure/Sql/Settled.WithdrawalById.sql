SELECT CONCAT('wd_', LOWER(REPLACE(CONVERT(char(36), WithdrawalId), '-', ''))) AS Reference, -Amount AS Amount, Currency, ProviderReference
FROM payments.Withdrawals WHERE WithdrawalId = @Id AND Status = 5;
