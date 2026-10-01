INSERT INTO payments.Withdrawals (WithdrawalId, UserId, AccountId, Amount, Currency, Provider, Status, RequiresApproval, CreatedAt, UpdatedAt)
VALUES (@WithdrawalId, @UserId, @AccountId, @Amount, @Currency, @Provider, @Status, @RequiresApproval, @CreatedAt, @CreatedAt);
