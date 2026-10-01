using Dapper;
using Microsoft.Data.SqlClient;
using SwiftBets.BuildingBlocks.Outbox;
using SwiftBets.BuildingBlocks.Persistence;
using SwiftBets.Payments.Application.Ports;
using SwiftBets.Payments.Domain;
using SwiftBets.Payments.Infrastructure.Messaging;

namespace SwiftBets.Payments.Infrastructure.Persistence;

/// <summary>Payments in SQL Server. Each status change is a conditional update that commits with its outbox events.</summary>
public sealed class SqlPaymentStore(ISqlConnectionFactory connections, IOutbox outbox) : IPaymentStore
{
    private static readonly SqlResources Sql = SqlResources.For<SqlPaymentStore>();

    public async Task InsertDepositAsync(Deposit deposit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Deposits.Insert"), new
        {
            deposit.PaymentId,
            deposit.UserId,
            deposit.AccountId,
            deposit.Amount,
            deposit.Currency,
            deposit.Provider,
            Status = (byte)deposit.Status,
            deposit.CreatedAt,
        }, cancellationToken: cancellationToken));
    }

    public async Task<Deposit?> GetDepositAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<DepositRow>(new CommandDefinition(Sql.Get("Deposits.Get"), new { PaymentId = paymentId }, cancellationToken: cancellationToken)))?.ToDomain();
    }

    public async Task<bool> MarkDepositPendingAsync(Guid paymentId, string providerReference, string checkoutUrl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Deposits.MarkPending"),
            new { PaymentId = paymentId, ProviderReference = providerReference, CheckoutUrl = checkoutUrl, Now = now }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> CompleteDepositAsync(Guid paymentId, DepositStatus status, string? failureReason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var completed = await connection.QuerySingleOrDefaultAsync<DepositRow>(new CommandDefinition(Sql.Get("Deposits.Complete"),
            new { PaymentId = paymentId, Status = (byte)status, FailureReason = failureReason, Now = now }, transaction, cancellationToken: cancellationToken));
        if (completed is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (PaymentContracts.ForDeposit(completed.ToDomain(), now) is { } message)
        {
            await message.Enqueue(outbox, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Deposit>> OpenDepositsAsync(DateTimeOffset updatedBefore, int batch, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. (await connection.QueryAsync<DepositRow>(new CommandDefinition(Sql.Get("Deposits.Open"), new { Before = updatedBefore, Batch = batch }, cancellationToken: cancellationToken))).Select(r => r.ToDomain())];
    }

    public async Task InsertWithdrawalAsync(Withdrawal withdrawal, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Withdrawals.Insert"), new
        {
            withdrawal.WithdrawalId,
            withdrawal.UserId,
            withdrawal.AccountId,
            withdrawal.Amount,
            withdrawal.Currency,
            withdrawal.Provider,
            Status = (byte)withdrawal.Status,
            withdrawal.RequiresApproval,
            withdrawal.CreatedAt,
        }, cancellationToken: cancellationToken));
    }

    public async Task<Withdrawal?> GetWithdrawalAsync(Guid withdrawalId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<WithdrawalRow>(new CommandDefinition(Sql.Get("Withdrawals.Get"), new { WithdrawalId = withdrawalId }, cancellationToken: cancellationToken)))?.ToDomain();
    }

    public async Task<bool> TransitionAsync(WithdrawalStatus from, Withdrawal next, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Withdrawals.Transition"), new
        {
            next.WithdrawalId,
            From = (byte)from,
            Status = (byte)next.Status,
            next.ReservationId,
            next.HoldSettled,
            next.ProviderReference,
            next.DecidedBy,
            next.Reason,
            IsFinal = next.IsFinal,
            Now = now,
        }, transaction, cancellationToken: cancellationToken));
        if (changed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        foreach (var message in PaymentContracts.ForWithdrawal(from, next, now))
        {
            await message.Enqueue(outbox, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkHoldSettledAsync(Guid withdrawalId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Withdrawals.MarkHoldSettled"), new { WithdrawalId = withdrawalId, Now = now }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Withdrawal>> UnfinishedWithdrawalsAsync(DateTimeOffset updatedBefore, int batch, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. (await connection.QueryAsync<WithdrawalRow>(new CommandDefinition(Sql.Get("Withdrawals.Unfinished"), new { Before = updatedBefore, Batch = batch }, cancellationToken: cancellationToken))).Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<Withdrawal>> WithdrawalsAsync(WithdrawalStatus status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. (await connection.QueryAsync<WithdrawalRow>(new CommandDefinition(Sql.Get("Withdrawals.ByStatus"), new { Status = (byte)status, Limit = limit }, cancellationToken: cancellationToken))).Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<PaymentHistoryItem>> HistoryAsync(Guid userId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. (await connection.QueryAsync<HistoryRow>(new CommandDefinition(Sql.Get("History.ForUser"), new { UserId = userId, Limit = limit }, cancellationToken: cancellationToken))).Select(r => r.ToItem())];
    }

    public async Task<bool> RecordWebhookAsync(ProviderEvent providerEvent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        try
        {
            return await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Webhooks.Record"), new
            {
                providerEvent.Provider,
                providerEvent.EventId,
                Kind = (byte)providerEvent.Kind,
                providerEvent.Reference,
                Now = now,
            }, cancellationToken: cancellationToken)) == 1;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SettledMovement>> SettledAsync(string provider, DateOnly day, CancellationToken cancellationToken)
    {
        var (from, to) = PaymentCalendar.Bounds(day);
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. (await connection.QueryAsync<SettledRow>(new CommandDefinition(Sql.Get("Settled.ForDay"), new { Provider = provider, From = from, To = to }, cancellationToken: cancellationToken))).Select(r => r.ToMovement())];
    }

    public async Task<SettledMovement?> SettledByReferenceAsync(string reference, CancellationToken cancellationToken)
    {
        var (query, id) = Deposit.IdFrom(reference) is { } paymentId ? ("Settled.DepositById", paymentId)
            : Withdrawal.IdFrom(reference) is { } withdrawalId ? ("Settled.WithdrawalById", withdrawalId)
            : (null, Guid.Empty);
        if (query is null)
        {
            return null;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<SettledRow>(new CommandDefinition(Sql.Get(query), new { Id = id }, cancellationToken: cancellationToken)))?.ToMovement();
    }

    public async Task SaveRunAsync(ReconciliationRun run, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Runs.Insert"), new
        {
            run.RunId,
            run.Provider,
            Day = run.Day.ToDateTime(TimeOnly.MinValue),
            DriftCount = run.Drifts.Count,
            run.CompletedAt,
        }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(Sql.Get("Runs.InsertDrift"), run.Drifts.Select(d => new
        {
            run.RunId,
            Kind = (byte)d.Kind,
            d.Reference,
            d.ProviderAmount,
            d.LedgerAmount,
            d.Currency,
            d.Detail,
        }), transaction, cancellationToken: cancellationToken));
        if (run.Drifts.Count > 0)
        {
            await PaymentContracts.ForDrift(run).Enqueue(outbox, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReconciliationRun?> LatestRunAsync(string provider, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(Sql.Get("Runs.Latest"), new { Provider = provider }, cancellationToken: cancellationToken));
        var run = await grid.ReadSingleOrDefaultAsync<RunRow>();
        var drifts = (await grid.ReadAsync<DriftRow>()).Select(d => d.ToDomain()).ToList();
        return run is null ? null : new ReconciliationRun(run.RunId, run.Provider, DateOnly.FromDateTime(run.Day), run.CompletedAt, drifts);
    }
}
