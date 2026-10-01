SELECT TOP (@Batch) WithdrawalId, UserId, AccountId, Amount, Currency, Provider, Status, ReservationId, RequiresApproval, HoldSettled, ProviderReference, DecidedBy, Reason, CreatedAt, CompletedAt
FROM payments.Withdrawals
WHERE UpdatedAt < @Before AND (HoldSettled = 0 OR Status IN (1, 3, 4)) AND Status <> 2
ORDER BY UpdatedAt;
