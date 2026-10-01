namespace SwiftBets.Payments.Domain.Tests;

public sealed class ReconcilerTests
{
    private static readonly SettledMovement Deposit = new("dep_a", 10_000, "ZAR", "p1");
    private static readonly SettledMovement Payout = new("wd_b", -5_000, "ZAR", "p2");

    [Fact]
    public void Matching_sides_have_no_drift() =>
        Reconciler.Compare([Deposit, Payout], [Deposit, Payout], _ => null, _ => null).ShouldBeEmpty();

    [Fact]
    public void A_settlement_the_ledger_never_recorded_is_missing_in_the_ledger()
    {
        var injected = new SettledMovement("dep_x", 7_500, "ZAR", "p9");

        var drifts = Reconciler.Compare([Deposit, injected], [Deposit], _ => null, _ => null);

        drifts.ShouldHaveSingleItem().ShouldBe(new PaymentDrift(DriftKind.MissingInLedger, "dep_x", 7_500, null, "ZAR", "the provider settled it; no successful record here"));
        Reconciler.NetDifference(drifts, "ZAR").ShouldBe(7_500);
    }

    [Fact]
    public void A_success_only_the_ledger_has_is_missing_at_the_provider()
    {
        var drifts = Reconciler.Compare([], [Payout], _ => null, _ => null);

        drifts.ShouldHaveSingleItem().Kind.ShouldBe(DriftKind.MissingAtProvider);
        Reconciler.NetDifference(drifts, "ZAR").ShouldBe(5_000);
    }

    [Fact]
    public void A_payment_that_settled_either_side_of_midnight_is_not_a_drift()
    {
        var lateDeposit = new SettledMovement("dep_late", 2_000, "ZAR", "p3");
        var earlyPayout = new SettledMovement("wd_early", -1_000, "ZAR", "p4");

        // The provider lists the deposit today but the ledger completed it tomorrow; the payout the other way round.
        Reconciler.Compare([lateDeposit], [earlyPayout], r => r == "dep_late" ? lateDeposit : null, r => r == "wd_early" ? earlyPayout : null).ShouldBeEmpty();
    }

    [Fact]
    public void Different_amounts_are_a_mismatch_from_either_side()
    {
        var drifts = Reconciler.Compare([Deposit with { Amount = 9_000 }], [Deposit], _ => null, _ => null);
        var reverse = Reconciler.Compare([], [Payout], _ => null, _ => Payout with { Amount = -4_000 });

        drifts.ShouldHaveSingleItem().Kind.ShouldBe(DriftKind.AmountMismatch);
        reverse.ShouldHaveSingleItem().Kind.ShouldBe(DriftKind.AmountMismatch);
    }
}
