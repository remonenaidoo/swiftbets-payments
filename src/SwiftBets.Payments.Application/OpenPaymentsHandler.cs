using Microsoft.Extensions.Logging;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

/// <summary>
/// Finishes payments a webhook never finished: asks the provider about open deposits and submitted withdrawals, retries
/// steps a crash interrupted, and gives up on deposits the customer abandoned.
/// </summary>
public sealed partial class OpenPaymentsHandler(
    IPaymentStore store, IPaymentProviders providers, DepositHandler deposits, WithdrawalHandler withdrawals, TimeProvider time,
    ILogger<OpenPaymentsHandler> logger)
{
    /// <summary>How long a payment may sit without news before the provider is asked about it.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(5);

    /// <summary>A deposit still open after this is treated as abandoned at checkout.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(24);

    public async Task<int> SweepAsync(int batch, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var touched = 0;
        foreach (var deposit in await store.OpenDepositsAsync(now - QuietFor, batch, cancellationToken))
        {
            touched += await TryAsync(deposit.PaymentId, () => SweepDepositAsync(deposit, now, cancellationToken));
        }

        foreach (var withdrawal in await store.UnfinishedWithdrawalsAsync(now - QuietFor, batch, cancellationToken))
        {
            touched += await TryAsync(withdrawal.WithdrawalId, () => withdrawals.ResumeAsync(withdrawal, cancellationToken));
        }

        return touched;
    }

    private async Task SweepDepositAsync(Deposit deposit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var provider = providers.Find(deposit.Provider) ?? providers.Deposits;
        if (deposit.Status == DepositStatus.Pending && await provider.DepositStatusAsync(deposit.Reference, cancellationToken) is { } status)
        {
            await deposits.ApplyAsync(status, cancellationToken);
            return;
        }

        if (deposit.Status == DepositStatus.Created || now - deposit.CreatedAt > AbandonAfter)
        {
            await store.CompleteDepositAsync(deposit.PaymentId, DepositStatus.Failed, "abandoned before payment", now, cancellationToken);
        }
    }

    private async Task<int> TryAsync(Guid id, Func<Task> step)
    {
        try
        {
            await step();
            return 1;
        }
        catch (Exception ex) when (ex is ProviderUnavailableException or WalletUnavailableException)
        {
            LogRetryLater(logger, id, ex.Message);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment {Id} could not be finished now and is retried on the next sweep: {Error}")]
    private static partial void LogRetryLater(ILogger logger, Guid id, string error);
}
