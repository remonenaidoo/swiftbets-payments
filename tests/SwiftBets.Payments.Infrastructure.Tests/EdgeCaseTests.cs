using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Infrastructure.Providers;
using SwiftBets.Payments.Simulator;

namespace SwiftBets.Payments.Infrastructure.Tests;

/// <summary>The paths a happy flow never takes: outages, mismatches, refusals and abandoned payments.</summary>
public sealed class EdgeCaseTests(SqlServerFixture sql)
{
    private static readonly Guid Customer = Guid.Parse("10000000-0000-0000-0000-000000000004");

    [Fact]
    public async Task A_provider_that_is_down_fails_the_deposit_at_once()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var providers = new PaymentProviders([new DownProvider()], Options.Create(new PaymentsOptions { DepositProvider = "down" }));
        var deposits = new DepositHandler(rig.Store, providers, rig.Wallet, PaymentRules.Default, rig.Time, NullLogger<DepositHandler>.Instance);

        (await deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Error.ShouldBe(PaymentError.ProviderUnavailable);
    }

    [Fact]
    public async Task A_success_for_a_different_amount_is_not_credited()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;

        var result = await rig.Deposits.ApplyAsync(new ProviderEvent("simulator", "e1", ProviderEventKind.DepositSucceeded, deposit.Reference, 9_000, "ZAR", "p", null), CancellationToken.None);

        result.ShouldBe(ApplyResult.Applied);
        rig.Wallet.Credited.ShouldBe(0);
        (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!.FailureReason.ShouldBe("provider confirmed 9000 ZAR, expected 5000 ZAR");
    }

    [Fact]
    public async Task Events_for_payments_that_are_not_ours_are_unknown()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);

        (await rig.Deposits.ApplyAsync(new ProviderEvent("simulator", "e", ProviderEventKind.DepositSucceeded, "dep_" + Guid.NewGuid().ToString("N"), 1, "ZAR", null, null), CancellationToken.None))
            .ShouldBe(ApplyResult.Unknown);
        (await rig.Withdrawals.ApplyAsync(new ProviderEvent("simulator", "e", ProviderEventKind.TransferSucceeded, "something-else", 1, "ZAR", null, null), CancellationToken.None))
            .ShouldBe(ApplyResult.Unknown);
        (await rig.Webhooks.ReceiveAsync("acme", [], _ => null, CancellationToken.None)).ShouldBe(WebhookOutcome.UnknownProvider);
    }

    [Fact]
    public async Task A_currency_the_wallet_will_not_open_is_refused_for_both_directions()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        rig.Wallet.RefuseAccounts = "currency_mismatch";

        (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Error!.Code.ShouldBe("currency_mismatch");
        (await rig.Withdrawals.RequestAsync(Customer, 10_000, "ZAR", CancellationToken.None)).Error!.Code.ShouldBe("currency_mismatch");
    }

    [Fact]
    public async Task Deciding_a_withdrawal_that_is_missing_or_not_waiting_is_refused()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        var small = (await rig.Withdrawals.RequestAsync(Customer, 10_000, "ZAR", CancellationToken.None)).Value!;

        (await rig.Withdrawals.ApproveAsync(Guid.NewGuid(), "ops", CancellationToken.None)).Error.ShouldBe(PaymentError.NotFound);
        (await rig.Withdrawals.RejectAsync(Guid.NewGuid(), "ops", "no", CancellationToken.None)).Error.ShouldBe(PaymentError.NotFound);
        (await rig.Withdrawals.RejectAsync(small.WithdrawalId, "ops", "too late", CancellationToken.None)).Error.ShouldBe(PaymentError.InvalidState);
    }

    [Fact]
    public async Task Deposits_left_open_are_given_up_as_abandoned()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var neverStarted = Deposit.Start(Customer, Customer, 5_000, "ZAR", "simulator", rig.Time.GetUtcNow());
        await rig.Store.InsertDepositAsync(neverStarted, CancellationToken.None);
        var unpaid = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;

        rig.Time.Advance(OpenPaymentsHandler.AbandonAfter + TimeSpan.FromMinutes(1));
        await rig.Sweep.SweepAsync(10, CancellationToken.None);

        (await rig.Store.GetDepositAsync(neverStarted.PaymentId, CancellationToken.None))!.FailureReason.ShouldBe("abandoned before payment");
        (await rig.Store.GetDepositAsync(unpaid.PaymentId, CancellationToken.None))!.Status.ShouldBe(DepositStatus.Failed);
    }

    [Fact]
    public async Task A_wallet_outage_leaves_the_payment_for_the_next_sweep()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        await rig.SetFaultsAsync(new SimFaults(HoldWebhooks: true));
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        await rig.CheckoutAsync(deposit);
        rig.Time.Advance(OpenPaymentsHandler.QuietFor + TimeSpan.FromMinutes(1));

        rig.Wallet.Down = true;
        (await rig.Sweep.SweepAsync(10, CancellationToken.None)).ShouldBe(0);
        rig.Wallet.Down = false;
        (await rig.Sweep.SweepAsync(10, CancellationToken.None)).ShouldBe(1);

        (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!.Status.ShouldBe(DepositStatus.Succeeded);
    }

    [Fact]
    public async Task A_hold_the_wallet_will_not_settle_stays_unsettled_for_the_sweep()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        rig.Wallet.RefuseSettlements = true;
        var requested = (await rig.Withdrawals.RequestAsync(Customer, 10_000, "ZAR", CancellationToken.None)).Value!;
        await rig.DeliveredAsync(1);
        (await rig.Store.GetWithdrawalAsync(requested.WithdrawalId, CancellationToken.None))!.HoldSettled.ShouldBeFalse();

        rig.Wallet.RefuseSettlements = false;
        rig.Time.Advance(OpenPaymentsHandler.QuietFor + TimeSpan.FromMinutes(1));
        await rig.Sweep.SweepAsync(10, CancellationToken.None);

        (await rig.Store.GetWithdrawalAsync(requested.WithdrawalId, CancellationToken.None))!.HoldSettled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_deposit_refunded_at_the_provider_after_it_was_credited_is_missing_at_the_provider()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        await rig.CheckoutAsync(deposit);
        await rig.DeliveredAsync(2);
        await rig.Provider.RefundAsync(deposit, "chargeback", CancellationToken.None);

        var run = await rig.Reconciliation.RunAsync("simulator", PaymentCalendar.DayOf(rig.Time.GetUtcNow()), CancellationToken.None);

        run.Drifts.ShouldHaveSingleItem().Kind.ShouldBe(DriftKind.MissingAtProvider);
    }

    private sealed class DownProvider : IPaymentProvider
    {
        public string Name => "down";

        public bool SupportsWithdrawals => false;

        public Task<ProviderCheckout> StartDepositAsync(Deposit deposit, string customerEmail, CancellationToken cancellationToken) =>
            throw new ProviderUnavailableException("down");

        public Task<ProviderEvent?> DepositStatusAsync(string reference, CancellationToken cancellationToken) => throw new ProviderUnavailableException("down");

        public Task<string> StartTransferAsync(Withdrawal withdrawal, CancellationToken cancellationToken) => throw new ProviderUnavailableException("down");

        public Task<ProviderEvent?> TransferStatusAsync(string reference, CancellationToken cancellationToken) => throw new ProviderUnavailableException("down");

        public Task<IReadOnlyList<SettledMovement>> SettledAsync(DateOnly day, CancellationToken cancellationToken) => throw new ProviderUnavailableException("down");

        public Task RefundAsync(Deposit deposit, string reason, CancellationToken cancellationToken) => throw new ProviderUnavailableException("down");

        public WebhookResult ReadWebhook(byte[] body, Func<string, string?> header, DateTimeOffset now) => WebhookResult.Rejected;
    }
}
