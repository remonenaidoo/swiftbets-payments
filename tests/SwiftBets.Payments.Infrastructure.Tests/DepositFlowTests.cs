using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.Contracts.Messaging;
using SwiftBets.Payments.Application;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Simulator;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace SwiftBets.Payments.Infrastructure.Tests;

public sealed class DepositFlowTests(SqlServerFixture sql)
{
    private static readonly Guid Customer = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task A_deposit_is_credited_exactly_once_when_the_success_webhook_arrives_twice_and_out_of_order()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        await rig.SetFaultsAsync(new SimFaults(DuplicateWebhooks: true, ReverseOrder: true));
        var deposit = (await rig.Deposits.StartAsync(Customer, 25_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;

        await rig.CheckoutAsync(deposit);
        await rig.DeliveredAsync(3);

        rig.Deliveries.ShouldAllBe(status => status == System.Net.HttpStatusCode.OK);
        rig.Wallet.Credited.ShouldBe(25_000);
        rig.Wallet.Keys.ShouldBe([deposit.WalletKey]);
        (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!.Status.ShouldBe(DepositStatus.Succeeded);
        (await rig.OutboxCountAsync(Topics.DepositSucceeded)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_success_publish_once()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        var paid = new SimPayment(deposit.ProviderReference!, deposit.Reference, false, 5_000, "ZAR", null);
        var (body, signature) = WebhookSender.Sign(PaymentsRig.WebhookSecret, "deposit.succeeded", paid, rig.Time.GetUtcNow(), "evt_same");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            rig.Webhooks.ReceiveAsync("simulator", body, name => name == "X-Simulator-Signature" ? signature : null, CancellationToken.None)));

        outcomes.ShouldAllBe(o => o == WebhookOutcome.Accepted);
        rig.Wallet.Credited.ShouldBe(5_000);
        (await rig.OutboxCountAsync(Topics.DepositSucceeded)).ShouldBe(1);
    }

    [Fact]
    public async Task A_declined_checkout_fails_the_deposit_and_credits_nothing()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;

        await rig.CheckoutAsync(deposit, pay: false);
        await rig.DeliveredAsync(2);

        rig.Wallet.Credited.ShouldBe(0);
        var failed = (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!;
        (failed.Status, failed.FailureReason).ShouldBe((DepositStatus.Failed, "declined by the customer"));
        (await rig.OutboxCountAsync(Topics.DepositFailed)).ShouldBe(1);
    }

    [Fact]
    public async Task A_deposit_the_wallet_refuses_fails_and_the_money_is_refunded_at_the_provider()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Wallet.RefuseDeposits = "limit_exceeded";
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;

        await rig.CheckoutAsync(deposit);
        await rig.DeliveredAsync(2);

        (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!.FailureReason.ShouldBe("refused by the wallet: deposit limit per day: 0 left");
        (await rig.Provider.DepositStatusAsync(deposit.Reference, CancellationToken.None))!.Kind.ShouldBe(ProviderEventKind.DepositFailed);
    }

    [Fact]
    public async Task A_webhook_with_a_bad_signature_is_refused_and_changes_nothing()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        var paid = new SimPayment("sim_x", deposit.Reference, false, 5_000, "ZAR", null);
        var (body, _) = WebhookSender.Sign(PaymentsRig.WebhookSecret, "deposit.succeeded", paid, rig.Time.GetUtcNow());
        var (_, forged) = WebhookSender.Sign("not-the-secret", "deposit.succeeded", paid, rig.Time.GetUtcNow());

        (await rig.Webhooks.ReceiveAsync("simulator", body, _ => forged, CancellationToken.None)).ShouldBe(WebhookOutcome.Unauthentic);
        (await rig.Webhooks.ReceiveAsync("simulator", body, _ => null, CancellationToken.None)).ShouldBe(WebhookOutcome.Unauthentic);

        rig.Wallet.Calls.ShouldBe(0);
        (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!.Status.ShouldBe(DepositStatus.Pending);
    }

    [Fact]
    public async Task A_deposit_whose_webhooks_never_arrive_is_finished_by_the_sweep()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        await rig.SetFaultsAsync(new SimFaults(HoldWebhooks: true));
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        await rig.CheckoutAsync(deposit);

        rig.Time.Advance(OpenPaymentsHandler.QuietFor + TimeSpan.FromMinutes(1));
        await rig.Sweep.SweepAsync(10, CancellationToken.None);

        (await rig.Store.GetDepositAsync(deposit.PaymentId, CancellationToken.None))!.Status.ShouldBe(DepositStatus.Succeeded);
        rig.Wallet.Credited.ShouldBe(5_000);
    }

    [Fact]
    public async Task Amounts_outside_the_bounds_never_reach_the_provider()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);

        (await rig.Deposits.StartAsync(Customer, 10, "ZAR", "c@example.com", CancellationToken.None)).Error.ShouldBe(PaymentError.AmountOutOfRange);
        (await rig.Deposits.StartAsync(Customer, 10_000, "EUR", "c@example.com", CancellationToken.None)).Error.ShouldBe(PaymentError.CurrencyNotSupported);
        rig.Wallet.Calls.ShouldBe(0);
    }
}
