using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.Contracts.Messaging;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Simulator;

namespace SwiftBets.Payments.Infrastructure.Tests;

public sealed class WithdrawalFlowTests(SqlServerFixture sql)
{
    private static readonly Guid Customer = Guid.Parse("10000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task A_customer_who_has_not_verified_their_identity_cannot_withdraw()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);

        var outcome = await rig.Withdrawals.RequestAsync(Customer, 10_000, "ZAR", CancellationToken.None);

        outcome.Error.ShouldBe(PaymentError.KycRequired);
        rig.Wallet.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_small_withdrawal_is_held_paid_out_and_its_hold_paid_to_the_provider()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);

        var requested = (await rig.Withdrawals.RequestAsync(Customer, 20_000, "ZAR", CancellationToken.None)).Value!;
        await rig.DeliveredAsync(1);

        // The simulator pays out at once, so its webhook can win the race with recording the submission.
        requested.Status.ShouldBeOneOf(WithdrawalStatus.Submitted, WithdrawalStatus.Paid);
        var paid = (await rig.Store.GetWithdrawalAsync(requested.WithdrawalId, CancellationToken.None))!;
        (paid.Status, paid.HoldSettled).ShouldBe((WithdrawalStatus.Paid, true));
        rig.Wallet.HoldState(paid.ReservationId).ShouldBe("paid");
        (await rig.OutboxCountAsync(Topics.WithdrawalRequested)).ShouldBe(1);
        (await rig.OutboxCountAsync(Topics.WithdrawalPaid)).ShouldBe(1);
    }

    [Fact]
    public async Task A_large_withdrawal_waits_for_an_operator_and_a_rejection_returns_the_hold()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);

        var waiting = (await rig.Withdrawals.RequestAsync(Customer, 600_000, "ZAR", CancellationToken.None)).Value!;
        var rejected = (await rig.Withdrawals.RejectAsync(waiting.WithdrawalId, "ops-1", "source of funds unclear", CancellationToken.None)).Value!;

        waiting.Status.ShouldBe(WithdrawalStatus.AwaitingApproval);
        (rejected.Status, rejected.HoldSettled, rejected.DecidedBy).ShouldBe((WithdrawalStatus.Rejected, true, "ops-1"));
        rig.Wallet.HoldState(rejected.ReservationId).ShouldBe("returned");
        (await rig.OutboxCountAsync(Topics.WithdrawalDecided)).ShouldBe(1);
        (await rig.OutboxCountAsync(Topics.WithdrawalFailed)).ShouldBe(1);
        (await rig.Withdrawals.ApproveAsync(waiting.WithdrawalId, "ops-2", CancellationToken.None)).Error.ShouldBe(PaymentError.InvalidState);
    }

    [Fact]
    public async Task An_approved_large_withdrawal_is_paid_out()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        var waiting = (await rig.Withdrawals.RequestAsync(Customer, 600_000, "ZAR", CancellationToken.None)).Value!;

        (await rig.Withdrawals.ApproveAsync(waiting.WithdrawalId, "ops-1", CancellationToken.None)).Error.ShouldBeNull();
        await rig.DeliveredAsync(1);

        (await rig.Store.GetWithdrawalAsync(waiting.WithdrawalId, CancellationToken.None))!.Status.ShouldBe(WithdrawalStatus.Paid);
        rig.Wallet.PaidOut.ShouldBe(1);
    }

    [Fact]
    public async Task A_transfer_the_bank_rejects_returns_the_hold_to_the_customer()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        await rig.SetFaultsAsync(new SimFaults(FailTransfers: true, DuplicateWebhooks: true));

        var requested = (await rig.Withdrawals.RequestAsync(Customer, 20_000, "ZAR", CancellationToken.None)).Value!;
        await rig.DeliveredAsync(2);

        var failed = (await rig.Store.GetWithdrawalAsync(requested.WithdrawalId, CancellationToken.None))!;
        (failed.Status, failed.Reason, failed.HoldSettled).ShouldBe((WithdrawalStatus.Failed, "beneficiary bank rejected the transfer", true));
        rig.Wallet.HoldState(failed.ReservationId).ShouldBe("returned");
        (await rig.OutboxCountAsync(Topics.WithdrawalFailed)).ShouldBe(1);
    }

    [Fact]
    public async Task A_withdrawal_the_wallet_refuses_is_reported_and_publishes_nothing()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        rig.Wallet.RefuseHolds = "insufficient_funds";

        var outcome = await rig.Withdrawals.RequestAsync(Customer, 20_000, "ZAR", CancellationToken.None);

        outcome.Error!.Code.ShouldBe("insufficient_funds");
        (await rig.OutboxCountAsync(Topics.WithdrawalRequested)).ShouldBe(0);
    }

    [Fact]
    public async Task A_withdrawal_whose_payout_news_never_arrives_is_finished_by_the_sweep()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        await rig.SetFaultsAsync(new SimFaults(HoldWebhooks: true));
        var requested = (await rig.Withdrawals.RequestAsync(Customer, 20_000, "ZAR", CancellationToken.None)).Value!;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        rig.Time.Advance(OpenPaymentsHandler.QuietFor + TimeSpan.FromMinutes(1));
        await rig.Sweep.SweepAsync(10, CancellationToken.None);

        var paid = (await rig.Store.GetWithdrawalAsync(requested.WithdrawalId, CancellationToken.None))!;
        (paid.Status, paid.HoldSettled).ShouldBe((WithdrawalStatus.Paid, true));
    }

    [Fact]
    public async Task A_withdrawal_recorded_before_a_crash_is_held_and_submitted_by_the_sweep()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var orphan = Withdrawal.Request(Customer, Customer, 20_000, "ZAR", "simulator", requiresApproval: false, rig.Time.GetUtcNow());
        await rig.Store.InsertWithdrawalAsync(orphan, CancellationToken.None);

        rig.Time.Advance(OpenPaymentsHandler.QuietFor + TimeSpan.FromMinutes(1));
        await rig.Sweep.SweepAsync(10, CancellationToken.None);
        await rig.DeliveredAsync(1);

        (await rig.Store.GetWithdrawalAsync(orphan.WithdrawalId, CancellationToken.None))!.Status.ShouldBe(WithdrawalStatus.Paid);
        rig.Wallet.Keys.ShouldContain(orphan.HoldKey);
    }
}
