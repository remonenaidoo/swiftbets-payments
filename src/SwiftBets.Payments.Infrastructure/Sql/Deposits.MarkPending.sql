UPDATE payments.Deposits
SET Status = 2, ProviderReference = @ProviderReference, CheckoutUrl = @CheckoutUrl, UpdatedAt = @Now
WHERE PaymentId = @PaymentId AND Status = 1;
