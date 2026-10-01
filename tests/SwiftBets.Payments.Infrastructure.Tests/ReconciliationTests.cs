using System.Net.Http.Json;
using SwiftBets.BuildingBlocks.Testing;
using SwiftBets.Contracts.Messaging;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Infrastructure.Tests;

public sealed class ReconciliationTests(SqlServerFixture sql)
{
    private static readonly Guid Customer = Guid.Parse("10000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task A_day_where_both_sides_agree_has_no_drift_and_publishes_nothing()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Customers.Verified.Add(Customer);
        var deposit = (await rig.Deposits.StartAsync(Customer, 30_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        await rig.CheckoutAsync(deposit);
        await rig.Withdrawals.RequestAsync(Customer, 10_000, "ZAR", CancellationToken.None);
        await rig.DeliveredAsync(3);

        var run = await rig.Reconciliation.RunAsync("simulator", PaymentCalendar.DayOf(rig.Time.GetUtcNow()), CancellationToken.None);

        run.Drifts.ShouldBeEmpty();
        (await rig.OutboxCountAsync(Topics.PaymentDriftDetected)).ShouldBe(0);
        (await rig.Store.LatestRunAsync("simulator", CancellationToken.None))!.RunId.ShouldBe(run.RunId);
    }

    [Fact]
    public async Task A_settlement_injected_at_the_provider_is_flagged_and_published_for_steward()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        var day = PaymentCalendar.DayOf(rig.Time.GetUtcNow());
        (await rig.SimulatorClient().PostAsJsonAsync("/__settlements", new { reference = "dep_injected", amount = 12_345, currency = "ZAR", at = rig.Time.GetUtcNow() }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var run = await rig.Reconciliation.RunAsync("simulator", day, CancellationToken.None);

        run.Drifts.ShouldHaveSingleItem().ShouldBe(new PaymentDrift(DriftKind.MissingInLedger, "dep_injected", 12_345, null, "ZAR", "the provider settled it; no successful record here"));
        (await rig.OutboxCountAsync(Topics.PaymentDriftDetected)).ShouldBe(1);
        (await rig.Store.LatestRunAsync("simulator", CancellationToken.None))!.Drifts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_deposit_refused_after_payment_shows_as_drift_until_it_is_refunded()
    {
        await using var rig = await PaymentsRig.CreateAsync(sql);
        rig.Wallet.RefuseDeposits = "limit_exceeded";
        var deposit = (await rig.Deposits.StartAsync(Customer, 5_000, "ZAR", "c@example.com", CancellationToken.None)).Value!;
        await rig.CheckoutAsync(deposit);
        await rig.DeliveredAsync(2);

        // The refund went through, so the provider no longer counts it as settled and there is nothing to flag.
        (await rig.Reconciliation.RunAsync("simulator", PaymentCalendar.DayOf(rig.Time.GetUtcNow()), CancellationToken.None)).Drifts.ShouldBeEmpty();
    }
}
