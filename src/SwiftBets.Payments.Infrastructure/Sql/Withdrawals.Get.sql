SELECT WithdrawalId, UserId, AccountId, Amount, Currency, Provider, Status, ReservationId, RequiresApproval, HoldSettled, ProviderReference, DecidedBy, Reason, CreatedAt, CompletedAt
FROM payments.Withdrawals
WHERE WithdrawalId = @WithdrawalId;
