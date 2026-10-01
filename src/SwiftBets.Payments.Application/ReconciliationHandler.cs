using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

/// <summary>
/// The daily provider-against-ledger check: what the provider says it settled on a day against the deposits we
/// credited and the payouts we recorded. Drifts are stored and published for Steward.
/// </summary>
public sealed class ReconciliationHandler(IPaymentStore store, IPaymentProviders providers, TimeProvider time)
{
    public async Task<ReconciliationRun> RunAsync(string providerName, DateOnly day, CancellationToken cancellationToken)
    {
        var provider = providers.Find(providerName) ?? throw new ArgumentException($"Unknown provider '{providerName}'.", nameof(providerName));
        var theirs = await provider.SettledAsync(day, cancellationToken);
        var ours = await store.SettledAsync(provider.Name, day, cancellationToken);

        // References one side lists for the day are looked up on the other side before they can count as drift.
        var ourRefs = ours.Select(m => m.Reference).ToHashSet(StringComparer.Ordinal);
        var theirRefs = theirs.Select(m => m.Reference).ToHashSet(StringComparer.Ordinal);
        var ledgerElsewhere = new Dictionary<string, SettledMovement?>(StringComparer.Ordinal);
        foreach (var reference in theirRefs.Where(r => !ourRefs.Contains(r)))
        {
            ledgerElsewhere[reference] = await store.SettledByReferenceAsync(reference, cancellationToken);
        }

        var providerElsewhere = new Dictionary<string, SettledMovement?>(StringComparer.Ordinal);
        foreach (var reference in ourRefs.Where(r => !theirRefs.Contains(r)))
        {
            providerElsewhere[reference] = await LookUpAsync(provider, reference, cancellationToken);
        }

        var drifts = Reconciler.Compare(theirs, ours, r => ledgerElsewhere.GetValueOrDefault(r), r => providerElsewhere.GetValueOrDefault(r));
        var run = new ReconciliationRun(Guid.CreateVersion7(), provider.Name, day, time.GetUtcNow(), drifts);
        await store.SaveRunAsync(run, cancellationToken);
        return run;
    }

    private static async Task<SettledMovement?> LookUpAsync(IPaymentProvider provider, string reference, CancellationToken cancellationToken)
    {
        if (Deposit.IdFrom(reference) is not null)
        {
            return await provider.DepositStatusAsync(reference, cancellationToken) is { Kind: ProviderEventKind.DepositSucceeded } paid
                ? new SettledMovement(reference, paid.Amount ?? 0, paid.Currency ?? "", paid.ProviderReference)
                : null;
        }

        return await provider.TransferStatusAsync(reference, cancellationToken) is { Kind: ProviderEventKind.TransferSucceeded } sent
            ? new SettledMovement(reference, -(sent.Amount ?? 0), sent.Currency ?? "", sent.ProviderReference)
            : null;
    }
}
