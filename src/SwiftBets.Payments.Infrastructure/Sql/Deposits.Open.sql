SELECT TOP (@Batch) PaymentId, UserId, AccountId, Amount, Currency, Provider, Status, ProviderReference, CheckoutUrl, FailureReason, CreatedAt, CompletedAt
FROM payments.Deposits
WHERE Status IN (1, 2) AND UpdatedAt < @Before
ORDER BY UpdatedAt;
