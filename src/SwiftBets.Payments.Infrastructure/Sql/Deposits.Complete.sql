-- Only an open deposit becomes final, so of two concurrent completions exactly one changes a row.
UPDATE payments.Deposits
SET Status = @Status, FailureReason = @FailureReason, UpdatedAt = @Now, CompletedAt = @Now
OUTPUT inserted.PaymentId, inserted.UserId, inserted.AccountId, inserted.Amount, inserted.Currency, inserted.Provider, inserted.Status, inserted.ProviderReference, inserted.CheckoutUrl, inserted.FailureReason, inserted.CreatedAt, inserted.CompletedAt
WHERE PaymentId = @PaymentId AND Status IN (1, 2);
