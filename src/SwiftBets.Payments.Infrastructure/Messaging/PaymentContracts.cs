using System.Data.Common;
using SwiftBets.BuildingBlocks.Core;
using SwiftBets.BuildingBlocks.Outbox;
using SwiftBets.Contracts.Messaging;
using SwiftBets.Contracts.Payments;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;
using Money = SwiftBets.Contracts.Money.Money;

namespace SwiftBets.Payments.Infrastructure.Messaging;

/// <summary>A message on its way to the outbox, in the caller's transaction.</summary>
internal sealed record Outgoing(string Topic, Func<IOutbox, DbTransaction, CancellationToken, Task> Enqueue);

/// <summary>What each state change means to the rest of the platform. Customer events are keyed by user id.</summary>
internal static class PaymentContracts
{
    public static Outgoing? ForDeposit(Deposit completed, DateTimeOffset now) => completed.Status switch
    {
        DepositStatus.Succeeded => Out(Topics.DepositSucceeded, completed.UserId.ToString(), new DepositSucceededV1(
            completed.PaymentId, completed.UserId, new Money(completed.Amount, completed.Currency), completed.Provider, completed.ProviderReference ?? completed.Reference, now), now),
        DepositStatus.Failed => Out(Topics.DepositFailed, completed.UserId.ToString(), new DepositFailedV1(
            completed.PaymentId, completed.UserId, new Money(completed.Amount, completed.Currency), completed.Provider, completed.FailureReason ?? "failed", now), now),
        _ => null,
    };

    /// <summary>A refused hold (Requested → Failed) publishes nothing: the customer was told at once and no money moved.</summary>
    public static IEnumerable<Outgoing> ForWithdrawal(WithdrawalStatus from, Withdrawal next, DateTimeOffset now)
    {
        var key = next.UserId.ToString();
        var amount = new Money(next.Amount, next.Currency);
        switch (from, next.Status)
        {
            case (WithdrawalStatus.Requested, WithdrawalStatus.AwaitingApproval or WithdrawalStatus.Approved):
                yield return Out(Topics.WithdrawalRequested, key, new WithdrawalRequestedV1(next.WithdrawalId, next.UserId, amount, next.RequiresApproval, now), now);
                break;
            case (WithdrawalStatus.AwaitingApproval, WithdrawalStatus.Approved):
                yield return Out(Topics.WithdrawalDecided, key, new WithdrawalDecidedV1(next.WithdrawalId, next.UserId, true, next.DecidedBy ?? "operator", null, now), now);
                break;
            case (WithdrawalStatus.AwaitingApproval, WithdrawalStatus.Rejected):
                yield return Out(Topics.WithdrawalDecided, key, new WithdrawalDecidedV1(next.WithdrawalId, next.UserId, false, next.DecidedBy ?? "operator", next.Reason, now), now);
                yield return Out(Topics.WithdrawalFailed, key, new WithdrawalFailedV1(next.WithdrawalId, next.UserId, amount, $"rejected: {next.Reason}", now), now);
                break;
            case (WithdrawalStatus.Approved or WithdrawalStatus.Submitted, WithdrawalStatus.Paid):
                yield return Out(Topics.WithdrawalPaid, key, new WithdrawalPaidV1(next.WithdrawalId, next.UserId, amount, next.Provider, next.ProviderReference ?? next.Reference, now), now);
                break;
            case (WithdrawalStatus.Approved or WithdrawalStatus.Submitted, WithdrawalStatus.Failed):
                yield return Out(Topics.WithdrawalFailed, key, new WithdrawalFailedV1(next.WithdrawalId, next.UserId, amount, next.Reason ?? "failed", now), now);
                break;
        }
    }

    public static Outgoing ForDrift(ReconciliationRun run)
    {
        var currency = run.Drifts[0].Currency;
        var summary = string.Join("; ", run.Drifts.GroupBy(d => d.Kind).Select(g => $"{g.Count()} {g.Key}"));
        return Out(Topics.PaymentDriftDetected, run.Provider, new PaymentDriftDetectedV1(
            run.RunId, run.Provider, run.Day, run.Drifts.Count, new Money(Reconciler.NetDifference(run.Drifts, currency), currency), summary, run.CompletedAt), run.CompletedAt);
    }

    private static Outgoing Out<T>(string topic, string key, T payload, DateTimeOffset now)
        where T : IEventContract
    {
        var envelope = Envelope(payload, now);
        return new(topic, (outbox, transaction, cancellationToken) => outbox.EnqueueAsync(transaction, topic, key, envelope, cancellationToken));
    }

    private static EventEnvelope<T> Envelope<T>(T payload, DateTimeOffset now)
        where T : IEventContract =>
        EventEnvelope<T>.Create(payload, now, CorrelationContext.CorrelationId ?? CorrelationContext.NewId());
}
