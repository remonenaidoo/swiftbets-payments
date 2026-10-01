using System.Collections.Concurrent;
using SwiftBets.Payments.Application.Ports;

namespace SwiftBets.Payments.Infrastructure.Tests;

/// <summary>
/// A wallet that keeps the real one's promise: a repeated key returns the first answer and moves nothing again. It
/// records every call so tests can count what actually moved.
/// </summary>
public sealed class FakeWallet : IWalletClient
{
    private readonly ConcurrentDictionary<string, WalletResult> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _holds = new();
    private long _credited;
    private long _held;
    private long _paidOut;
    private int _calls;

    public string? RefuseDeposits { get; set; }

    public string? RefuseHolds { get; set; }

    public string? RefuseAccounts { get; set; }

    public bool RefuseSettlements { get; set; }

    /// <summary>While set, every call fails as if the wallet were down.</summary>
    public bool Down { get; set; }

    public long Credited => Interlocked.Read(ref _credited);

    public long Held => Interlocked.Read(ref _held);

    public long PaidOut => Interlocked.Read(ref _paidOut);

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyCollection<string> Keys => [.. _byKey.Keys];

    public Task<WalletResult> OpenAccountAsync(Guid userId, string currency, CancellationToken cancellationToken) =>
        Task.FromResult(RefuseAccounts is { } code ? WalletResult.Refused(code, "currency not offered") : WalletResult.Ok(userId));

    public Task<WalletResult> DepositAsync(string key, Guid accountId, long amount, string currency, string reference, CancellationToken cancellationToken) =>
        Keyed(key, () =>
        {
            if (RefuseDeposits is { } code)
            {
                return WalletResult.Refused(code, "deposit limit per day: 0 left");
            }

            Interlocked.Add(ref _credited, amount);
            return WalletResult.Ok(Guid.NewGuid());
        });

    public Task<WalletResult> HoldWithdrawalAsync(string key, Guid accountId, long amount, string currency, string reference, CancellationToken cancellationToken) =>
        Keyed(key, () =>
        {
            if (RefuseHolds is { } code)
            {
                return WalletResult.Refused(code, "not enough money");
            }

            var reservationId = Guid.NewGuid();
            _holds[reservationId] = "held";
            Interlocked.Add(ref _held, amount);
            return WalletResult.Ok(reservationId);
        });

    public Task<WalletResult> CompleteWithdrawalAsync(string key, Guid reservationId, CancellationToken cancellationToken) =>
        Keyed(key, () => Settle(reservationId, "paid"));

    public Task<WalletResult> ReturnWithdrawalAsync(string key, Guid reservationId, CancellationToken cancellationToken) =>
        Keyed(key, () => Settle(reservationId, "returned"));

    public string? HoldState(Guid? reservationId) => reservationId is { } id ? _holds.GetValueOrDefault(id) : null;

    private WalletResult Settle(Guid reservationId, string outcome)
    {
        if (RefuseSettlements)
        {
            return WalletResult.Refused("invalid_state", "refused for the test");
        }

        if (!_holds.TryUpdate(reservationId, outcome, "held"))
        {
            return WalletResult.Refused("invalid_state", "reservation is not held");
        }

        if (outcome == "paid")
        {
            Interlocked.Increment(ref _paidOut);
        }

        return WalletResult.Ok(reservationId);
    }

    private Task<WalletResult> Keyed(string key, Func<WalletResult> apply)
    {
        if (Down)
        {
            throw new WalletUnavailableException("wallet down for the test");
        }

        Interlocked.Increment(ref _calls);
        // Like the real wallet, only a movement is kept under its key; a refusal moved nothing and may be tried again.
        lock (_byKey)
        {
            if (_byKey.TryGetValue(key, out var earlier))
            {
                return Task.FromResult(earlier);
            }

            var result = apply();
            if (result.Succeeded)
            {
                _byKey[key] = result;
            }

            return Task.FromResult(result);
        }
    }
}

public sealed class FakeCustomers : ICustomerStatus
{
    public HashSet<Guid> Verified { get; } = [];

    public bool IsKycVerified(Guid userId) => Verified.Contains(userId);
}
