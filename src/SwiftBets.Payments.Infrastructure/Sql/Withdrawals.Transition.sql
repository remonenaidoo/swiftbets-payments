UPDATE payments.Withdrawals
SET Status = @Status, ReservationId = @ReservationId, HoldSettled = @HoldSettled, ProviderReference = @ProviderReference,
    DecidedBy = @DecidedBy, Reason = @Reason, UpdatedAt = @Now, CompletedAt = CASE WHEN @IsFinal = 1 THEN @Now ELSE NULL END
WHERE WithdrawalId = @WithdrawalId AND Status = @From;
