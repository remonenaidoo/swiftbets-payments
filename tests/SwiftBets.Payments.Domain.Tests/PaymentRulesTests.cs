namespace SwiftBets.Payments.Domain.Tests;

public sealed class PaymentRulesTests
{
    private readonly PaymentRules _rules = PaymentRules.Default;

    [Theory]
    [InlineData(999, "ZAR", "amount_out_of_range")]
    [InlineData(5_000_001, "ZAR", "amount_out_of_range")]
    [InlineData(10_000, "EUR", "currency_not_supported")]
    [InlineData(10_000, "ZAR", null)]
    public void Deposit_bounds(long amount, string currency, string? code) =>
        _rules.CheckDeposit(amount, currency)?.Code.ShouldBe(code);

    [Fact]
    public void An_unverified_customer_cannot_withdraw_even_a_valid_amount()
    {
        _rules.CheckWithdrawal(10_000, "ZAR", kycVerified: false).ShouldBe(PaymentError.KycRequired);
        _rules.CheckWithdrawal(10_000, "ZAR", kycVerified: true).ShouldBeNull();
        _rules.CheckWithdrawal(1_000, "ZAR", kycVerified: true).ShouldBe(PaymentError.AmountOutOfRange);
    }

    [Fact]
    public void Only_amounts_above_the_threshold_need_an_operator()
    {
        _rules.NeedsApproval(500_000, "ZAR").ShouldBeFalse();
        _rules.NeedsApproval(500_001, "ZAR").ShouldBeTrue();
        _rules.NeedsApproval(30_001, "USD").ShouldBeTrue();
    }

    [Fact]
    public void References_round_trip_and_reject_each_others_prefix()
    {
        var id = Guid.CreateVersion7();

        Deposit.IdFrom(Deposit.ReferenceFor(id)).ShouldBe(id);
        Withdrawal.IdFrom(Withdrawal.ReferenceFor(id)).ShouldBe(id);
        Deposit.IdFrom(Withdrawal.ReferenceFor(id)).ShouldBeNull();
        Deposit.IdFrom("dep_not-a-guid").ShouldBeNull();
    }

    [Fact]
    public void A_reconciliation_day_is_the_south_african_calendar_day()
    {
        var justAfterLocalMidnight = new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero);

        PaymentCalendar.DayOf(justAfterLocalMidnight).ShouldBe(new DateOnly(2026, 10, 1));
        PaymentCalendar.Bounds(new DateOnly(2026, 10, 1)).From.ShouldBe(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_withdrawal_above_the_threshold_waits_for_approval_after_its_hold()
    {
        var big = Withdrawal.Request(Guid.NewGuid(), Guid.NewGuid(), 600_000, "ZAR", "simulator", requiresApproval: true, DateTimeOffset.UnixEpoch);

        big.AfterHold.ShouldBe(WithdrawalStatus.AwaitingApproval);
        (big with { RequiresApproval = false }).AfterHold.ShouldBe(WithdrawalStatus.Approved);
        (big with { Status = WithdrawalStatus.Paid }).PaysOut.ShouldBeTrue();
        (big with { Status = WithdrawalStatus.Rejected }).PaysOut.ShouldBeFalse();
    }
}
