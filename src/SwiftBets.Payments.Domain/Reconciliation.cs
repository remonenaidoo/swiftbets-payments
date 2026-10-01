namespace SwiftBets.Payments.Domain;

public enum DriftKind : byte
{
    /// <summary>The provider settled money we have no successful record of: a deposit not credited or a payout not recorded.</summary>
    MissingInLedger = 1,

    /// <summary>We recorded a success the provider does not know as settled.</summary>
    MissingAtProvider = 2,

    /// <summary>Both sides know it, for different amounts or currencies.</summary>
    AmountMismatch = 3,
}

/// <summary>One settled movement as one side reports it. Amount is positive for money in (deposits), negative for payouts.</summary>
public sealed record SettledMovement(string Reference, long Amount, string Currency, string? ProviderReference);

public sealed record PaymentDrift(DriftKind Kind, string Reference, long? ProviderAmount, long? LedgerAmount, string Currency, string Detail);

/// <summary>
/// Compares a provider's settled movements for a day against ours. A reference only one side lists for the day is
/// looked up on the other side before it counts, so a payment that settled either side of midnight is not a drift.
/// </summary>
public static class Reconciler
{
    public static IReadOnlyList<PaymentDrift> Compare(
        IReadOnlyList<SettledMovement> provider,
        IReadOnlyList<SettledMovement> ledger,
        Func<string, SettledMovement?> ledgerLookup,
        Func<string, SettledMovement?> providerLookup)
    {
        var drifts = new List<PaymentDrift>();
        var ours = ledger.ToDictionary(m => m.Reference, StringComparer.Ordinal);
        var theirs = provider.ToDictionary(m => m.Reference, StringComparer.Ordinal);

        foreach (var p in provider)
        {
            var l = ours.GetValueOrDefault(p.Reference) ?? ledgerLookup(p.Reference);
            if (l is null)
            {
                drifts.Add(new(DriftKind.MissingInLedger, p.Reference, p.Amount, null, p.Currency, "the provider settled it; no successful record here"));
            }
            else if (l.Amount != p.Amount || l.Currency != p.Currency)
            {
                drifts.Add(new(DriftKind.AmountMismatch, p.Reference, p.Amount, l.Amount, p.Currency, $"provider {p.Amount} {p.Currency}, ledger {l.Amount} {l.Currency}"));
            }
        }

        foreach (var l in ledger.Where(l => !theirs.ContainsKey(l.Reference)))
        {
            var p = providerLookup(l.Reference);
            if (p is null)
            {
                drifts.Add(new(DriftKind.MissingAtProvider, l.Reference, null, l.Amount, l.Currency, "recorded as settled here; the provider does not have it settled"));
            }
            else if (p.Amount != l.Amount || p.Currency != l.Currency)
            {
                drifts.Add(new(DriftKind.AmountMismatch, l.Reference, p.Amount, l.Amount, l.Currency, $"provider {p.Amount} {p.Currency}, ledger {l.Amount} {l.Currency}"));
            }
        }

        return drifts;
    }

    /// <summary>Provider total minus ledger total for one currency: positive means the provider holds more than we credited.</summary>
    public static long NetDifference(IReadOnlyList<PaymentDrift> drifts, string currency) =>
        drifts.Where(d => d.Currency == currency).Sum(d => (d.ProviderAmount ?? 0) - (d.LedgerAmount ?? 0));
}
