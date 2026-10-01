UPDATE payments.Withdrawals SET HoldSettled = 1, UpdatedAt = @Now WHERE WithdrawalId = @WithdrawalId;
