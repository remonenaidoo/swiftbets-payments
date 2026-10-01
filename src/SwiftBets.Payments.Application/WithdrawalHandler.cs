using Microsoft.Extensions.Logging;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

/// <summary>
/// Withdrawals: identity verified, amount held in the wallet, operator approval above the threshold, payout through the
/// provider, then the hold paid out or returned. The status changes first and the wallet is told after, so every step
/// can be finished again by <see cref="ResumeAsync"/> if the process stops in between.
/// </summary>
public sealed partial class WithdrawalHandler(
    IPaymentStore store, IPaymentProviders providers, IWalletClient wallet, ICustomerStatus customers, PaymentRules rules, TimeProvider time,
    ILogger<WithdrawalHandler> logger)
{
    public async Task<PaymentOutcome<Withdrawal>> RequestAsync(Guid userId, long amount, string currency, CancellationToken cancellationToken)
    {
        if (rules.CheckWithdrawal(amount, currency, customers.IsKycVerified(userId)) is { } invalid)
        {
            return PaymentOutcome<Withdrawal>.Fail(invalid);
        }

        var account = await wallet.OpenAccountAsync(userId, currency, cancellationToken);
        if (!account.Succeeded)
        {
            return PaymentOutcome<Withdrawal>.Fail(new PaymentError(account.FailureCode ?? "wallet_refused", account.Message ?? "The wallet refused the account."));
        }

        var withdrawal = Withdrawal.Request(userId, account.Id!.Value, amount, currency, providers.Withdrawals.Name, rules.NeedsApproval(amount, currency), time.GetUtcNow());
        await store.InsertWithdrawalAsync(withdrawal, cancellationToken);
        return await HoldAsync(withdrawal, cancellationToken);
    }

    public async Task<PaymentOutcome<Withdrawal>> ApproveAsync(Guid withdrawalId, string operatorId, CancellationToken cancellationToken)
    {
        if (await store.GetWithdrawalAsync(withdrawalId, cancellationToken) is not { } withdrawal)
        {
            return PaymentOutcome<Withdrawal>.Fail(PaymentError.NotFound);
        }

        var approved = withdrawal with { Status = WithdrawalStatus.Approved, DecidedBy = operatorId };
        if (withdrawal.Status != WithdrawalStatus.AwaitingApproval || !await store.TransitionAsync(WithdrawalStatus.AwaitingApproval, approved, time.GetUtcNow(), cancellationToken))
        {
            return PaymentOutcome<Withdrawal>.Fail(PaymentError.InvalidState);
        }

        return PaymentOutcome<Withdrawal>.Ok(await SubmitAsync(approved, cancellationToken));
    }

    public async Task<PaymentOutcome<Withdrawal>> RejectAsync(Guid withdrawalId, string operatorId, string reason, CancellationToken cancellationToken)
    {
        if (await store.GetWithdrawalAsync(withdrawalId, cancellationToken) is not { } withdrawal)
        {
            return PaymentOutcome<Withdrawal>.Fail(PaymentError.NotFound);
        }

        var rejected = withdrawal with { Status = WithdrawalStatus.Rejected, DecidedBy = operatorId, Reason = reason };
        if (withdrawal.Status != WithdrawalStatus.AwaitingApproval || !await store.TransitionAsync(WithdrawalStatus.AwaitingApproval, rejected, time.GetUtcNow(), cancellationToken))
        {
            return PaymentOutcome<Withdrawal>.Fail(PaymentError.InvalidState);
        }

        return PaymentOutcome<Withdrawal>.Ok(await SettleHoldAsync(rejected, cancellationToken));
    }

    /// <exception cref="WalletUnavailableException">The wallet did not answer; the provider's retry or the reconciler finishes it.</exception>
    public async Task<ApplyResult> ApplyAsync(ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        if (Withdrawal.IdFrom(providerEvent.Reference) is not { } id)
        {
            return ApplyResult.Unknown;
        }

        // The submission may be recorded between our read and our write (the provider can answer that fast), so a
        // lost race is read again rather than taken as the event being stale.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await store.GetWithdrawalAsync(id, cancellationToken) is not { } withdrawal)
            {
                return ApplyResult.Unknown;
            }

            // The provider may answer before the submission was recorded, so Approved counts as submitted here.
            if (withdrawal.Status is not (WithdrawalStatus.Submitted or WithdrawalStatus.Approved)
                || providerEvent.Kind is not (ProviderEventKind.TransferSucceeded or ProviderEventKind.TransferFailed))
            {
                if (withdrawal.IsFinal && !withdrawal.HoldSettled)
                {
                    await SettleHoldAsync(withdrawal, cancellationToken);
                }

                return ApplyResult.Ignored;
            }

            var next = providerEvent.Kind == ProviderEventKind.TransferSucceeded
                ? withdrawal with { Status = WithdrawalStatus.Paid, ProviderReference = providerEvent.ProviderReference ?? withdrawal.ProviderReference }
                : withdrawal with { Status = WithdrawalStatus.Failed, Reason = providerEvent.FailureReason ?? "the provider could not pay it out" };
            if (await store.TransitionAsync(withdrawal.Status, next, time.GetUtcNow(), cancellationToken))
            {
                await SettleHoldAsync(next, cancellationToken);
                return ApplyResult.Applied;
            }
        }

        return ApplyResult.Ignored;
    }

    /// <summary>Takes a withdrawal left mid-way one step further; safe to repeat because every step is keyed or conditional.</summary>
    public async Task ResumeAsync(Withdrawal withdrawal, CancellationToken cancellationToken)
    {
        switch (withdrawal.Status)
        {
            case WithdrawalStatus.Requested:
                await HoldAsync(withdrawal, cancellationToken);
                break;
            case WithdrawalStatus.Approved:
                await SubmitAsync(withdrawal, cancellationToken);
                break;
            case WithdrawalStatus.Submitted:
                var provider = providers.Find(withdrawal.Provider) ?? providers.Withdrawals;
                if (await provider.TransferStatusAsync(withdrawal.Reference, cancellationToken) is { } status)
                {
                    await ApplyAsync(status, cancellationToken);
                }

                break;
            default:
                if (withdrawal.IsFinal && !withdrawal.HoldSettled)
                {
                    await SettleHoldAsync(withdrawal, cancellationToken);
                }

                break;
        }
    }

    private async Task<PaymentOutcome<Withdrawal>> HoldAsync(Withdrawal withdrawal, CancellationToken cancellationToken)
    {
        var hold = await wallet.HoldWithdrawalAsync(withdrawal.HoldKey, withdrawal.AccountId, withdrawal.Amount, withdrawal.Currency, withdrawal.Reference, cancellationToken);
        if (!hold.Succeeded)
        {
            // Nothing was held, so there is nothing to settle.
            var refused = withdrawal with { Status = WithdrawalStatus.Failed, Reason = hold.Message, HoldSettled = true };
            await store.TransitionAsync(WithdrawalStatus.Requested, refused, time.GetUtcNow(), cancellationToken);
            return PaymentOutcome<Withdrawal>.Fail(new PaymentError(Code(hold.FailureCode), hold.Message ?? "The wallet refused the withdrawal."));
        }

        var held = withdrawal with { Status = withdrawal.AfterHold, ReservationId = hold.Id };
        if (!await store.TransitionAsync(WithdrawalStatus.Requested, held, time.GetUtcNow(), cancellationToken))
        {
            return PaymentOutcome<Withdrawal>.Ok((await store.GetWithdrawalAsync(withdrawal.WithdrawalId, cancellationToken))!);
        }

        return PaymentOutcome<Withdrawal>.Ok(held.Status == WithdrawalStatus.Approved ? await SubmitAsync(held, cancellationToken) : held);
    }

    private async Task<Withdrawal> SubmitAsync(Withdrawal approved, CancellationToken cancellationToken)
    {
        var provider = providers.Find(approved.Provider) ?? providers.Withdrawals;
        try
        {
            var reference = await provider.StartTransferAsync(approved, cancellationToken);
            var submitted = approved with { Status = WithdrawalStatus.Submitted, ProviderReference = reference };
            return await store.TransitionAsync(WithdrawalStatus.Approved, submitted, time.GetUtcNow(), cancellationToken)
                ? submitted
                : (await store.GetWithdrawalAsync(approved.WithdrawalId, cancellationToken))!;
        }
        catch (ProviderUnavailableException ex)
        {
            // Left approved; the reconciler submits it again under the same reference.
            LogSubmitFailed(logger, approved.WithdrawalId, ex.Message);
            return approved;
        }
    }

    private async Task<Withdrawal> SettleHoldAsync(Withdrawal final, CancellationToken cancellationToken)
    {
        if (final.HoldSettled || final.ReservationId is not { } reservationId)
        {
            return final;
        }

        var settled = final.PaysOut
            ? await wallet.CompleteWithdrawalAsync(final.PayKey, reservationId, cancellationToken)
            : await wallet.ReturnWithdrawalAsync(final.ReturnKey, reservationId, cancellationToken);
        if (!settled.Succeeded)
        {
            LogHoldUnsettled(logger, final.WithdrawalId, final.Status, settled.FailureCode, settled.Message);
            return final;
        }

        await store.MarkHoldSettledAsync(final.WithdrawalId, time.GetUtcNow(), cancellationToken);
        return final with { HoldSettled = true };
    }

    private static string Code(string? walletCode) => walletCode switch
    {
        "insufficient_funds" => "insufficient_funds",
        "account_restricted" => "withdrawals_blocked",
        "account_blacklisted" => "account_blocked",
        _ => "wallet_refused",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Submitting withdrawal {WithdrawalId} failed and will be retried: {Error}")]
    private static partial void LogSubmitFailed(ILogger logger, Guid withdrawalId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Withdrawal {WithdrawalId} is {Status} but its wallet hold could not be settled: {Code} {Error}")]
    private static partial void LogHoldUnsettled(ILogger logger, Guid withdrawalId, WithdrawalStatus status, string? code, string? error);
}
