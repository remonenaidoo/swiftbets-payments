SELECT PaymentId, UserId, AccountId, Amount, Currency, Provider, Status, ProviderReference, CheckoutUrl, FailureReason, CreatedAt, CompletedAt
FROM payments.Deposits
WHERE PaymentId = @PaymentId;
