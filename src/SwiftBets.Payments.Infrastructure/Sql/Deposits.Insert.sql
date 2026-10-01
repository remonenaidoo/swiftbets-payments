INSERT INTO payments.Deposits (PaymentId, UserId, AccountId, Amount, Currency, Provider, Status, CreatedAt, UpdatedAt)
VALUES (@PaymentId, @UserId, @AccountId, @Amount, @Currency, @Provider, @Status, @CreatedAt, @CreatedAt);
