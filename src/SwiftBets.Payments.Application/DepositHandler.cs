using Microsoft.Extensions.Logging;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

/// <summary>
/// Starts deposits and applies what providers report about them. The wallet is credited under the deposit's own key
/// before the deposit is marked succeeded, and marking is conditional, so duplicate or reordered webhooks credit once.
/// </summary>
public sealed partial class DepositHandler(
    IPaymentStore store, IPaymentProviders providers, IWalletClient wallet, PaymentRules rules, TimeProvider time, ILogger<DepositHandler> logger)
{
    public async Task<PaymentOutcome<Deposit>> StartAsync(Guid userId, long amount, string currency, string customerEmail, CancellationToken cancellationToken)
    {
        if (rules.CheckDeposit(amount, currency) is { } invalid)
        {
            return PaymentOutcome<Deposit>.Fail(invalid);
        }

        var account = await wallet.OpenAccountAsync(userId, currency, cancellationToken);
        if (!account.Succeeded)
        {
            return PaymentOutcome<Deposit>.Fail(new PaymentError(account.FailureCode ?? "wallet_refused", account.Message ?? "The wallet refused the account."));
        }

        var provider = providers.Deposits;
        var deposit = Deposit.Start(userId, account.Id!.Value, amount, currency, provider.Name, time.GetUtcNow());
        await store.InsertDepositAsync(deposit, cancellationToken);
        try
        {
            var checkout = await provider.StartDepositAsync(deposit, customerEmail, cancellationToken);
            await store.MarkDepositPendingAsync(deposit.PaymentId, checkout.ProviderReference, checkout.CheckoutUrl, time.GetUtcNow(), cancellationToken);
            return PaymentOutcome<Deposit>.Ok(deposit with { Status = DepositStatus.Pending, ProviderReference = checkout.ProviderReference, CheckoutUrl = checkout.CheckoutUrl });
        }
        catch (ProviderUnavailableException ex)
        {
            LogProviderDown(logger, provider.Name, deposit.PaymentId, ex.Message);
            await store.CompleteDepositAsync(deposit.PaymentId, DepositStatus.Failed, "provider unavailable", time.GetUtcNow(), CancellationToken.None);
            return PaymentOutcome<Deposit>.Fail(PaymentError.ProviderUnavailable);
        }
    }

    /// <exception cref="WalletUnavailableException">The wallet did not answer; the provider's retry or the reconciler finishes it.</exception>
    public async Task<ApplyResult> ApplyAsync(ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        if (Deposit.IdFrom(providerEvent.Reference) is not { } paymentId || await store.GetDepositAsync(paymentId, cancellationToken) is not { } deposit)
        {
            return ApplyResult.Unknown;
        }

        if (deposit.IsFinal || providerEvent.Kind == ProviderEventKind.DepositPending)
        {
            return ApplyResult.Ignored;
        }

        if (providerEvent.Kind == ProviderEventKind.DepositFailed)
        {
            return await FinishAsync(deposit, DepositStatus.Failed, providerEvent.FailureReason ?? "declined by the provider");
        }

        if (providerEvent.Kind != ProviderEventKind.DepositSucceeded)
        {
            return ApplyResult.Ignored;
        }

        // A success for a different amount is not credited; the daily reconciliation reports the provider's side.
        if (providerEvent.Amount is { } paid && (paid != deposit.Amount || providerEvent.Currency != deposit.Currency))
        {
            LogAmountMismatch(logger, deposit.PaymentId, deposit.Amount, paid);
            return await FinishAsync(deposit, DepositStatus.Failed, $"provider confirmed {paid} {providerEvent.Currency}, expected {deposit.Amount} {deposit.Currency}");
        }

        var credit = await wallet.DepositAsync(deposit.WalletKey, deposit.AccountId, deposit.Amount, deposit.Currency, deposit.Reference, cancellationToken);
        if (credit.Succeeded)
        {
            return await FinishAsync(deposit, DepositStatus.Succeeded, null);
        }

        // The customer paid but a limit or restriction stops the credit: the money goes back to them.
        var reason = $"refused by the wallet: {credit.Message}";
        var result = await FinishAsync(deposit, DepositStatus.Failed, reason);
        if (result == ApplyResult.Applied)
        {
            await RefundAsync(deposit, reason);
        }

        return result;
    }

    private async Task<ApplyResult> FinishAsync(Deposit deposit, DepositStatus status, string? reason) =>
        await store.CompleteDepositAsync(deposit.PaymentId, status, reason, time.GetUtcNow(), CancellationToken.None) ? ApplyResult.Applied : ApplyResult.Ignored;

    private async Task RefundAsync(Deposit deposit, string reason)
    {
        try
        {
            await (providers.Find(deposit.Provider) ?? providers.Deposits).RefundAsync(deposit, reason, CancellationToken.None);
        }
        catch (ProviderUnavailableException ex)
        {
            // The reconciliation lists the provider's settled payment against a failed deposit until someone refunds it.
            LogRefundFailed(logger, deposit.PaymentId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider {Provider} unavailable starting deposit {PaymentId}: {Error}")]
    private static partial void LogProviderDown(ILogger logger, string provider, Guid paymentId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deposit {PaymentId} confirmed for {Paid}, expected {Expected}; not credited")]
    private static partial void LogAmountMismatch(ILogger logger, Guid paymentId, long expected, long paid);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refund of refused deposit {PaymentId} failed: {Error}")]
    private static partial void LogRefundFailed(ILogger logger, Guid paymentId, string error);
}
