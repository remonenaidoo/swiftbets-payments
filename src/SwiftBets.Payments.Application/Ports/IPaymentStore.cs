using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application.Ports;

/// <summary>One deposit or withdrawal as the customer sees it in their payment history. Kind is deposit or withdrawal.</summary>
public sealed record PaymentHistoryItem(string Kind, Guid Id, long Amount, string Currency, string Status, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record ReconciliationRun(Guid RunId, string Provider, DateOnly Day, DateTimeOffset CompletedAt, IReadOnlyList<PaymentDrift> Drifts);

/// <summary>
/// Payments state. Every status change is conditional on the status the caller read, and commits with the events it
/// implies, so two deliveries of the same webhook cannot both win.
/// </summary>
public interface IPaymentStore
{
    Task InsertDepositAsync(Deposit deposit, CancellationToken cancellationToken);

    Task<Deposit?> GetDepositAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Created → Pending once the provider has the deposit; false if it moved on meanwhile.</summary>
    Task<bool> MarkDepositPendingAsync(Guid paymentId, string providerReference, string checkoutUrl, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Created or Pending → Succeeded or Failed, publishing the outcome; false if it was already final.</summary>
    Task<bool> CompleteDepositAsync(Guid paymentId, DepositStatus status, string? failureReason, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Deposit>> OpenDepositsAsync(DateTimeOffset updatedBefore, int batch, CancellationToken cancellationToken);

    Task InsertWithdrawalAsync(Withdrawal withdrawal, CancellationToken cancellationToken);

    Task<Withdrawal?> GetWithdrawalAsync(Guid withdrawalId, CancellationToken cancellationToken);

    /// <summary>Moves a withdrawal on from <paramref name="from"/> and publishes what the change means; false if it had moved on.</summary>
    Task<bool> TransitionAsync(WithdrawalStatus from, Withdrawal next, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkHoldSettledAsync(Guid withdrawalId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Withdrawals left mid-way (not final, or final with the hold unsettled) and untouched since <paramref name="updatedBefore"/>.</summary>
    Task<IReadOnlyList<Withdrawal>> UnfinishedWithdrawalsAsync(DateTimeOffset updatedBefore, int batch, CancellationToken cancellationToken);

    Task<IReadOnlyList<Withdrawal>> WithdrawalsAsync(WithdrawalStatus status, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentHistoryItem>> HistoryAsync(Guid userId, int limit, CancellationToken cancellationToken);

    /// <summary>Records a webhook event; false if this provider event id was seen before.</summary>
    Task<bool> RecordWebhookAsync(ProviderEvent providerEvent, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Our successful deposits (positive) and payouts (negative) with this provider completed on the day.</summary>
    Task<IReadOnlyList<SettledMovement>> SettledAsync(string provider, DateOnly day, CancellationToken cancellationToken);

    Task<SettledMovement?> SettledByReferenceAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Stores the run and, when it found drifts, publishes them, in one transaction.</summary>
    Task SaveRunAsync(ReconciliationRun run, CancellationToken cancellationToken);

    Task<ReconciliationRun?> LatestRunAsync(string provider, CancellationToken cancellationToken);
}
