using Microsoft.Extensions.Options;
using SwiftBets.Payments.Application.Ports;

namespace SwiftBets.Payments.Infrastructure.Providers;

public sealed class PaymentProviders(IEnumerable<IPaymentProvider> providers, IOptions<PaymentsOptions> options) : IPaymentProviders
{
    private readonly IReadOnlyList<IPaymentProvider> _all = [.. providers];

    public IPaymentProvider Deposits => Find(options.Value.DepositProvider) ?? throw new InvalidOperationException($"Deposit provider '{options.Value.DepositProvider}' is not registered.");

    public IPaymentProvider Withdrawals => Find(options.Value.WithdrawalProvider) is { SupportsWithdrawals: true } provider
        ? provider
        : throw new InvalidOperationException($"Withdrawal provider '{options.Value.WithdrawalProvider}' is not registered or cannot pay out.");

    public IReadOnlyList<IPaymentProvider> All => _all;

    public IPaymentProvider? Find(string name) => _all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}
