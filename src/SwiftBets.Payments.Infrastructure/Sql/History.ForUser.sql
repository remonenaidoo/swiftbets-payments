SELECT TOP (@Limit) Kind, Id, Amount, Currency, Status, Reason, CreatedAt, CompletedAt
FROM (SELECT 'deposit' AS Kind, PaymentId AS Id, Amount, Currency, Status, FailureReason AS Reason, CreatedAt, CompletedAt
      FROM payments.Deposits WHERE UserId = @UserId
      UNION ALL
      SELECT 'withdrawal', WithdrawalId, Amount, Currency, Status, Reason, CreatedAt, CompletedAt
      FROM payments.Withdrawals WHERE UserId = @UserId) h
ORDER BY CreatedAt DESC;
