using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

public enum WebhookOutcome
{
    Accepted,
    Unauthentic,
    UnknownProvider,
}

/// <summary>
/// A provider's webhook: checked against its signature, each event recorded once by the provider's event id, then
/// applied. A repeat delivery is acknowledged without being applied again.
/// </summary>
public sealed class WebhookHandler(IPaymentProviders providers, IPaymentStore store, DepositHandler deposits, WithdrawalHandler withdrawals, TimeProvider time)
{
    /// <exception cref="WalletUnavailableException">Answer 5xx so the provider delivers again.</exception>
    public async Task<WebhookOutcome> ReceiveAsync(string providerName, byte[] body, Func<string, string?> header, CancellationToken cancellationToken)
    {
        if (providers.Find(providerName) is not { } provider)
        {
            return WebhookOutcome.UnknownProvider;
        }

        var result = provider.ReadWebhook(body, header, time.GetUtcNow());
        if (!result.Authentic)
        {
            return WebhookOutcome.Unauthentic;
        }

        foreach (var providerEvent in result.Events)
        {
            await ApplyAsync(providerEvent, cancellationToken);
            await store.RecordWebhookAsync(providerEvent, time.GetUtcNow(), cancellationToken);
        }

        return WebhookOutcome.Accepted;
    }

    // Applying is itself idempotent, so an event is recorded only after it was applied: a crash between the two
    // leads to one harmless re-application on the provider's retry, never to a lost event.
    private Task<ApplyResult> ApplyAsync(ProviderEvent providerEvent, CancellationToken cancellationToken) =>
        providerEvent.Kind is ProviderEventKind.TransferSucceeded or ProviderEventKind.TransferFailed
            ? withdrawals.ApplyAsync(providerEvent, cancellationToken)
            : deposits.ApplyAsync(providerEvent, cancellationToken);
}
